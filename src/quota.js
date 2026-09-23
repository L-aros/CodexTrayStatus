const fs = require('node:fs/promises')
const os = require('node:os')
const path = require('node:path')

const USAGE_URL = 'https://chatgpt.com/backend-api/wham/usage'
const REQUEST_TIMEOUT_MS = 10_000

function getCodexHome() {
  const configured = process.env.CODEX_HOME && process.env.CODEX_HOME.trim()
  return configured || path.join(os.homedir(), '.codex')
}

function getAuthPath() {
  return path.join(getCodexHome(), 'auth.json')
}

async function loadCredentials() {
  let raw
  try {
    raw = await fs.readFile(getAuthPath(), 'utf8')
  } catch {
    throw new Error(`未找到 Codex 登录信息：${getAuthPath()}`)
  }

  let auth
  try {
    auth = JSON.parse(raw)
  } catch {
    throw new Error('Codex auth.json 不是有效 JSON')
  }

  const tokens = auth && typeof auth.tokens === 'object' ? auth.tokens : {}
  const accessToken = tokens.access_token || tokens.accessToken
  if (auth.auth_mode !== 'chatgpt' || typeof accessToken !== 'string' || !accessToken) {
    throw new Error('当前不是可读取订阅额度的 ChatGPT 登录模式')
  }

  return { accessToken, accountId: tokens.account_id || tokens.accountId }
}

function asFiniteNumber(value) {
  return typeof value === 'number' && Number.isFinite(value) ? value : undefined
}

function normalizeEpoch(value) {
  const numeric = asFiniteNumber(value)
  if (numeric === undefined) return undefined
  return numeric < 10_000_000_000 ? numeric * 1000 : numeric
}

function chooseLabel(id, seconds) {
  const lowered = String(id).toLowerCase()
  if (lowered.includes('primary') || seconds === 18_000) return '5h'
  if (lowered.includes('secondary') || seconds === 604_800) return '7d'
  if (seconds && seconds % 86_400 === 0) return `${seconds / 86_400}d`
  if (seconds && seconds % 3_600 === 0) return `${seconds / 3_600}h`
  return String(id)
}

function parseRateLimits(payload, now = Date.now()) {
  const rateLimit = payload && typeof payload.rate_limit === 'object'
    ? payload.rate_limit
    : payload && typeof payload.rateLimit === 'object'
      ? payload.rateLimit
      : undefined
  if (!rateLimit) return []

  return Object.entries(rateLimit)
    .filter(([, raw]) => raw && typeof raw === 'object')
    .map(([id, raw]) => {
      const seconds = asFiniteNumber(raw.limit_window_seconds ?? raw.limitWindowSeconds)
      const used = asFiniteNumber(raw.used_percent ?? raw.usedPercent)
      const resetAt = normalizeEpoch(raw.reset_at ?? raw.resetAt ?? raw.resets_at ?? raw.resetsAt)
      const resetIn = asFiniteNumber(raw.resets_in_seconds ?? raw.resetsInSeconds)
      const resetsAt = resetAt || (resetIn !== undefined ? now + resetIn * 1000 : undefined)
      if (used === undefined && resetsAt === undefined) return undefined
      const usedPercent = used === undefined ? undefined : Math.min(100, Math.max(0, used))
      return {
        id,
        label: chooseLabel(id, seconds),
        usedPercent,
        remainingPercent: usedPercent === undefined ? undefined : 100 - usedPercent,
        resetsAt
      }
    })
    .filter(Boolean)
    .sort((a, b) => a.label.localeCompare(b.label, undefined, { numeric: true }))
}

async function fetchQuota() {
  try {
    const { accessToken, accountId } = await loadCredentials()
    const headers = {
      Authorization: `Bearer ${accessToken}`,
      Accept: 'application/json',
      'User-Agent': 'codex-cli'
    }
    if (accountId) headers['ChatGPT-Account-Id'] = accountId
    const controller = new AbortController()
    const timeout = setTimeout(() => controller.abort(), REQUEST_TIMEOUT_MS)
    const response = await fetch(USAGE_URL, { headers, signal: controller.signal })
    if (!response.ok) throw new Error(`额度接口返回 HTTP ${response.status}`)
    const limits = parseRateLimits(await response.json())
    if (!limits.length) throw new Error('额度接口未返回可显示的窗口')
    clearTimeout(timeout)
    return { limits, refreshedAt: Date.now(), source: 'official' }
  } catch (error) {
    const local = await fetchLocalQuota()
    if (local.limits.length) {
      return { ...local, source: 'local', warning: error?.name === 'AbortError' ? '官方额度请求超时，已回退本机会话数据' : `官方额度不可用，已回退本机会话数据` }
    }
    if (error && error.name === 'AbortError') throw new Error('额度请求超时')
    throw error
  }
}

function priceFor(model) {
  const name = String(model || '').toLowerCase()
  if (name.includes('gpt-5.6-sol')) return { input: 5, cached: 0.5, output: 30 }
  if (name.includes('gpt-5.6-terra')) return { input: 2, cached: 0.2, output: 12 }
  if (name.includes('gpt-5.6-luna')) return { input: 0.2, cached: 0.02, output: 1.2 }
  if (name.includes('gpt-5.4-mini')) return { input: 0.75, cached: 0.075, output: 4.5 }
  if (name.includes('gpt-5.4')) return { input: 2.5, cached: 0.25, output: 15 }
  if (name.includes('gpt-5-mini')) return { input: 0.25, cached: 0.025, output: 2 }
  return { input: 1.25, cached: 0.125, output: 10 }
}

function sameLocalDay(timestamp, reference) {
  const left = new Date(timestamp)
  return left.getFullYear() === reference.getFullYear() &&
    left.getMonth() === reference.getMonth() &&
    left.getDate() === reference.getDate()
}

// Codex sessions write an exact request delta in info.last_token_usage. We intentionally
// do not use total_token_usage here: it is a session-level running total and would overcount.
function summarizeTodayJsonl(content, now = new Date()) {
  const total = { input: 0, output: 0, estimatedCost: 0 }
  let model
  const seen = new Set()
  for (const line of content.split(/\r?\n/)) {
    if (!line.trim()) continue
    let event
    try {
      event = JSON.parse(line)
    } catch {
      continue
    }
    const payload = event && typeof event.payload === 'object' ? event.payload : undefined
    if (payload?.type === 'turn_context' && typeof payload.model === 'string') model = payload.model
    if (event?.type !== 'event_msg' || payload?.type !== 'token_count') continue
    const timestamp = Date.parse(event.timestamp || event.time || event.created_at || '')
    if (!Number.isFinite(timestamp) || !sameLocalDay(timestamp, now)) continue
    const usage = payload.info?.last_token_usage
    if (!usage || typeof usage !== 'object') continue
    const input = Math.max(0, Number(usage.input_tokens) || 0)
    const cached = Math.min(input, Math.max(0, Number(usage.cached_input_tokens) || 0))
    const output = Math.max(0, Number(usage.output_tokens) || 0)
    const eventModel = typeof payload.model === 'string' ? payload.model : model
    const signature = `${timestamp}|${input}|${cached}|${output}|${eventModel || ''}`
    if (seen.has(signature)) continue
    seen.add(signature)
    const price = priceFor(eventModel)
    total.input += input
    total.output += output
    total.estimatedCost += ((input - cached) * price.input + cached * price.cached + output * price.output) / 1e6
  }
  total.totalTokens = total.input + total.output
  total.estimatedCost = Math.round(total.estimatedCost * 10_000) / 10_000
  return total
}

async function collectJsonlFiles(root, entries, limit) {
  if (entries.length >= limit) return
  let children
  try {
    children = await fs.readdir(root, { withFileTypes: true })
  } catch {
    return
  }
  for (const child of children) {
    if (entries.length >= limit) return
    const filePath = path.join(root, child.name)
    if (child.isDirectory()) {
      await collectJsonlFiles(filePath, entries, limit)
    } else if (child.isFile() && child.name.endsWith('.jsonl')) {
      try {
        const stat = await fs.stat(filePath)
        entries.push({ filePath, mtimeMs: stat.mtimeMs })
      } catch {}
    }
  }
}

function localWindow(id, raw, observedAt) {
  if (!raw || typeof raw !== 'object') return undefined
  const windowMinutes = asFiniteNumber(raw.window_minutes ?? raw.windowMinutes)
  const usedPercent = asFiniteNumber(raw.used_percent ?? raw.usedPercent)
  const resetAt = normalizeEpoch(raw.resets_at ?? raw.reset_at ?? raw.resetsAt ?? raw.resetAt)
  const resetIn = asFiniteNumber(raw.resets_in_seconds ?? raw.reset_in_seconds ?? raw.resetsInSeconds)
  const resetsAt = resetAt || (resetIn === undefined ? undefined : observedAt + resetIn * 1000)
  if (usedPercent === undefined && resetsAt === undefined) return undefined
  const expired = resetsAt !== undefined && resetsAt <= Date.now()
  const used = expired ? 0 : Math.min(100, Math.max(0, usedPercent ?? 0))
  return {
    id,
    label: chooseLabel(id, windowMinutes === undefined ? undefined : windowMinutes * 60),
    usedPercent: used,
    remainingPercent: 100 - used,
    resetsAt
  }
}

function parseLocalRateLimits(content, mtimeMs = Date.now()) {
  let latest
  for (const line of content.split(/\r?\n/)) {
    if (!line.trim()) continue
    let event
    try {
      event = JSON.parse(line)
    } catch {
      continue
    }
    const payload = event && typeof event.payload === 'object' ? event.payload : undefined
    if (event?.type !== 'event_msg' || payload?.type !== 'token_count' || !payload.rate_limits) continue
    const observedAt = Date.parse(event.timestamp || event.time || event.created_at || '') || mtimeMs
    const primary = localWindow('primary', payload.rate_limits.primary, observedAt)
    const secondary = localWindow('secondary', payload.rate_limits.secondary, observedAt)
    const limits = [primary, secondary].filter(Boolean)
    if (limits.length && (!latest || observedAt >= latest.observedAt)) latest = { limits, observedAt }
  }
  return latest?.limits || []
}

async function fetchLocalQuota() {
  const files = []
  const codexHome = getCodexHome()
  await collectJsonlFiles(path.join(codexHome, 'sessions'), files, 150)
  await collectJsonlFiles(path.join(codexHome, 'archived_sessions'), files, 150)
  files.sort((left, right) => right.mtimeMs - left.mtimeMs)
  let newest = { limits: [], observedAt: 0 }
  for (const entry of files.slice(0, 100)) {
    try {
      const limits = parseLocalRateLimits(await fs.readFile(entry.filePath, 'utf8'), entry.mtimeMs)
      const candidateTime = entry.mtimeMs
      if (limits.length && candidateTime >= newest.observedAt) newest = { limits, observedAt: candidateTime }
    } catch {}
  }
  return { limits: newest.limits, refreshedAt: Date.now() }
}

async function fetchTodayUsage() {
  const codexHome = getCodexHome()
  const files = []
  await collectJsonlFiles(path.join(codexHome, 'sessions'), files, 150)
  await collectJsonlFiles(path.join(codexHome, 'archived_sessions'), files, 150)
  files.sort((left, right) => right.mtimeMs - left.mtimeMs)
  const totals = { input: 0, output: 0, totalTokens: 0, estimatedCost: 0 }
  for (const entry of files.slice(0, 100)) {
    try {
      const part = summarizeTodayJsonl(await fs.readFile(entry.filePath, 'utf8'))
      totals.input += part.input
      totals.output += part.output
      totals.totalTokens += part.totalTokens
      totals.estimatedCost += part.estimatedCost
    } catch {}
  }
  totals.estimatedCost = Math.round(totals.estimatedCost * 10_000) / 10_000
  return totals
}

module.exports = { getAuthPath, parseRateLimits, parseLocalRateLimits, fetchQuota, fetchTodayUsage, summarizeTodayJsonl }
