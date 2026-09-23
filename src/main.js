const { app, BrowserWindow, ipcMain, Menu, Tray, nativeImage, Notification, screen } = require('electron')
const path = require('node:path')
const { fetchQuota, fetchTodayUsage } = require('./quota')
const { mapTaskbarLayoutToDisplay, readWindowsTaskbarLayout } = require('./taskbar-position')

const REFRESH_MS = 60_000
let tray
let statusWindow
let refreshTimer
let positionTimer
let layoutTimer
let taskbarLayout
let statusWidth = 145
let isQuitting = false
let state = { limits: [], today: undefined, refreshedAt: undefined, error: '等待首次刷新' }

const ownsInstanceLock = app.requestSingleInstanceLock()
if (!ownsInstanceLock) app.quit()
app.on('second-instance', () => {
  if (!statusWindow || statusWindow.isDestroyed()) return
  void positionStatusLayer(true)
  statusWindow.showInactive()
})

function minutesUntil(timestamp) {
  if (!timestamp) return undefined
  return Math.max(0, Math.ceil((timestamp - Date.now()) / 60_000))
}

function formatCountdown(timestamp) {
  const minutes = minutesUntil(timestamp)
  if (minutes === undefined) return '重置时间未知'
  const days = Math.floor(minutes / 1_440)
  const hours = Math.floor((minutes % 1_440) / 60)
  const rest = minutes % 60
  return days ? `${days}天 ${hours}小时后` : hours ? `${hours}小时 ${rest}分钟后` : `${rest}分钟后`
}

function statusLines() {
  if (!state.limits.length) return [state.error || '暂无用量数据']
  return state.limits.map((limit) =>
    `${limit.label}：剩余  ${Math.round(limit.remainingPercent)}% · ${formatCountdown(limit.resetsAt)}重置`
  )
}

function primaryStatus() {
  const short = state.limits.find((limit) => limit.label === '5h') || state.limits[0]
  return short
    ? { label: short.label.toUpperCase(), value: `${Math.round(short.remainingPercent)}%` }
    : { label: 'CODEX', value: '--' }
}

function formatTokens(tokens) {
  if (!Number.isFinite(tokens)) return '--'
  if (tokens >= 1_000_000) return `${(tokens / 1_000_000).toFixed(2)}M`
  if (tokens >= 1_000) return `${(tokens / 1_000).toFixed(tokens >= 100_000 ? 0 : 1)}K`
  return String(Math.round(tokens))
}

function secondaryStatus() {
  if (!state.today) return { tokens: '统计中', cost: '' }
  return { tokens: `今日 ${formatTokens(state.today.totalTokens)}`, cost: `$${state.today.estimatedCost.toFixed(2)}` }
}

function buildTooltip() {
  return ['Codex 剩余用量', ...statusLines()].join('\n').slice(0, 127)
}

function iconColor() {
  const min = Math.min(...state.limits.map((item) => item.remainingPercent ?? 100), 100)
  if (min <= 15) return '#ef4444'
  if (min <= 40) return '#f59e0b'
  return '#22c55e'
}

// Windows 托盘通常只渲染 16–20px；用高对比色环表达风险，精确信息放在悬停提示和菜单中。
function createIcon() {
  const percent = state.limits[0] ? Math.round(state.limits[0].remainingPercent) : '--'
  const color = state.limits.length ? iconColor() : '#6b7280'
  const svg = `<svg width="64" height="64" xmlns="http://www.w3.org/2000/svg">
    <rect width="64" height="64" rx="12" fill="#111827"/>
    <rect x="4" y="4" width="56" height="56" rx="10" fill="none" stroke="${color}" stroke-width="5"/>
    <text x="32" y="40" text-anchor="middle" font-family="Segoe UI,Arial" font-size="${String(percent).length > 2 ? 23 : 27}" font-weight="700" fill="#f9fafb">${percent}</text>
  </svg>`
  return nativeImage.createFromDataURL(`data:image/svg+xml;base64,${Buffer.from(svg).toString('base64')}`).resize({ width: 32, height: 32 })
}

function rebuildTray() {
  tray.setImage(createIcon())
  tray.setToolTip(buildTooltip())
  const lines = statusLines()
  const menu = [
    { label: 'Codex Tray Status', enabled: false },
    ...lines.map((label) => ({ label, enabled: false })),
    { type: 'separator' },
    { label: '立即刷新', click: () => void refresh() },
    { label: '退出', click: () => app.quit() }
  ]
  tray.setContextMenu(Menu.buildFromTemplate(menu))
  updateStatusLayer()
}

function createStatusLayer() {
  statusWindow = new BrowserWindow({
    width: statusWidth,
    height: 48,
    show: false,
    frame: false,
    transparent: true,
    resizable: false,
    movable: false,
    focusable: false,
    skipTaskbar: true,
    hasShadow: false,
    alwaysOnTop: true,
    webPreferences: {
      preload: path.join(__dirname, 'status-preload.js'),
      contextIsolation: true,
      nodeIntegration: false
    }
  })
  statusWindow.setAlwaysOnTop(true, 'screen-saver')
  // Consume clicks instead of forwarding them to the taskbar beneath. Forwarding made a click on
  // the label behave like a click on whatever taskbar control happened to be behind it.
  statusWindow.setIgnoreMouseEvents(false)
  ipcMain.removeAllListeners('status:content-width')
  ipcMain.on('status:content-width', (event, measuredWidth) => {
    if (event.sender !== statusWindow?.webContents || !Number.isFinite(measuredWidth)) return
    const nextWidth = Math.max(112, Math.min(175, Math.ceil(measuredWidth)))
    if (Math.abs(nextWidth - statusWidth) < 2) return
    statusWidth = nextWidth
    void positionStatusLayer(false)
  })
  statusWindow.loadFile(path.join(__dirname, 'status.html'))
  statusWindow.once('ready-to-show', () => {
    void positionStatusLayer()
    updateStatusLayer()
    statusWindow.showInactive()
  })
  statusWindow.on('closed', () => { statusWindow = undefined })
}

async function positionStatusLayer(refreshNativeLayout = true) {
  if (!statusWindow || statusWindow.isDestroyed()) return
  const display = screen.getPrimaryDisplay()
  const { bounds, workArea } = display
  if (refreshNativeLayout || !taskbarLayout) {
    taskbarLayout = await readWindowsTaskbarLayout() || taskbarLayout
  }
  const width = statusWidth
  let taskbarHeight = Math.max(40, bounds.height - workArea.height)
  let isTop = workArea.y > bounds.y
  let notificationLeft

  if (taskbarLayout) {
    // Compare the full taskbar width with Electron's display width. This works whether the helper
    // returns physical pixels or Windows has already virtualized them to DIP.
    const mapped = mapTaskbarLayoutToDisplay(taskbarLayout, bounds)
    notificationLeft = mapped?.notificationLeft
    taskbarHeight = Math.max(32, mapped?.taskbarHeight ?? taskbarHeight)
    isTop = mapped?.isTop ?? isTop
  }

  const x = notificationLeft !== undefined
    ? Math.max(bounds.x + 8, notificationLeft - width - 4)
    : Math.max(bounds.x + 8, bounds.x + bounds.width - 320 - width)
  const y = isTop ? bounds.y : bounds.y + bounds.height - taskbarHeight
  statusWindow.setBounds({ x, y, width, height: taskbarHeight })
  statusWindow.setAlwaysOnTop(true, 'screen-saver')
  statusWindow.setOpacity(1)
  statusWindow.webContents.send('status:update', { taskbarHeight, primary: primaryStatus(), secondary: secondaryStatus(), tone: iconColor() })
  if (!statusWindow.isVisible()) statusWindow.showInactive()
}

function updateStatusLayer() {
  if (!statusWindow || statusWindow.isDestroyed()) return
  const display = screen.getPrimaryDisplay()
  const taskbarHeight = Math.max(32, display.bounds.height - display.workArea.height)
  statusWindow.webContents.send('status:update', { taskbarHeight, primary: primaryStatus(), secondary: secondaryStatus(), tone: iconColor() })
}

async function refresh({ silent = false } = {}) {
  const [quota, today] = await Promise.allSettled([fetchQuota(), fetchTodayUsage()])
  try {
    if (quota.status === 'fulfilled') {
      state = { ...state, ...quota.value, error: undefined }
    } else {
      throw quota.reason
    }
  } catch (error) {
    state = { ...state, error: error instanceof Error ? error.message : String(error) }
    if (!silent && Notification.isSupported()) {
      new Notification({ title: 'Codex Tray Status', body: state.error }).show()
    }
  }
  if (today.status === 'fulfilled') state.today = today.value
  rebuildTray()
}

if (ownsInstanceLock) app.whenReady().then(async () => {
  if (process.platform === 'darwin') app.dock.hide()
  tray = new Tray(createIcon())
  tray.on('click', () => void refresh())
  rebuildTray()
  createStatusLayer()
  await refresh({ silent: true })
  refreshTimer = setInterval(() => void refresh({ silent: true }), REFRESH_MS)
  screen.on('display-metrics-changed', () => void positionStatusLayer())
  // Explorer can rebuild its z-order. Reassert visibility cheaply; refresh native layout less often.
  positionTimer = setInterval(() => {
    if (!statusWindow || statusWindow.isDestroyed()) return
    statusWindow.setAlwaysOnTop(true, 'screen-saver')
    if (typeof statusWindow.moveTop === 'function') statusWindow.moveTop()
    if (!statusWindow.isVisible()) statusWindow.showInactive()
  }, 2000)
  layoutTimer = setInterval(() => void positionStatusLayer(true), 30_000)
})

app.on('window-all-closed', (event) => event.preventDefault())
app.on('before-quit', () => {
  isQuitting = true
  clearInterval(refreshTimer)
  clearInterval(positionTimer)
  clearInterval(layoutTimer)
  ipcMain.removeAllListeners('status:content-width')
})
