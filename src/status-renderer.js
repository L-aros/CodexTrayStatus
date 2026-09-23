const status = document.getElementById('status')
const primaryLabel = document.getElementById('primary-label')
const primaryValue = document.getElementById('primary-value')
const todayTokens = document.getElementById('today-tokens')
const todayCost = document.getElementById('today-cost')
const costDot = document.getElementById('cost-dot')

// This is a display-only taskbar component. Consume pointer activity so it cannot trigger the
// Start/menu/app buttons that sit directly behind the transparent window.
document.addEventListener('pointerdown', (event) => event.preventDefault())
document.addEventListener('click', (event) => event.preventDefault())

window.taskbarStatus.onUpdate(({ taskbarHeight, primary, secondary, tone }) => {
  // Conservative limits keep the widget visually subordinate to native taskbar controls.
  const mainSize = Math.max(12, Math.min(17, Math.round(taskbarHeight * 0.26)))
  const subSize = Math.max(9, Math.min(12, Math.round(taskbarHeight * 0.18)))
  status.style.setProperty('--primary-size', `${mainSize}px`)
  status.style.setProperty('--secondary-size', `${subSize}px`)
  status.style.setProperty('--tone', tone)
  primaryLabel.textContent = primary.label
  primaryValue.textContent = primary.value
  todayTokens.textContent = secondary.tokens
  todayCost.textContent = secondary.cost
  costDot.hidden = !secondary.cost
  requestAnimationFrame(() => {
    const measured = Math.max(primaryLabel.parentElement.scrollWidth, todayTokens.parentElement.scrollWidth) + 10
    window.taskbarStatus.reportContentWidth(measured)
  })
})
