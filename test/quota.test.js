const test = require('node:test')
const assert = require('node:assert/strict')
const { parseRateLimits, parseLocalRateLimits, summarizeTodayJsonl } = require('../src/quota')
const { mapTaskbarLayoutToDisplay } = require('../src/taskbar-position')

test('parses primary and secondary official quota windows', () => {
  const now = 1700000000000
  const payload = {
    rate_limit: {
      primary_window: { limit_window_seconds: 18000, used_percent: 3, resets_in_seconds: 600 },
      secondary_window: { limit_window_seconds: 604800, used_percent: 42, reset_at: 1700001000 }
    }
  }
  const limits = parseRateLimits(payload, now)
  assert.equal(limits[0].label, '5h')
  assert.equal(limits[0].remainingPercent, 97)
  assert.equal(limits[0].resetsAt, now + 600000)
  assert.equal(limits[1].label, '7d')
  assert.equal(limits[1].remainingPercent, 58)
})

test('sums today request deltas without repeated token snapshots', () => {
  const context = { type: 'event_msg', payload: { type: 'turn_context', model: 'gpt-5.6-luna' } }
  const message = {
    type: 'event_msg',
    timestamp: '2026-09-04T06:00:00Z',
    payload: { type: 'token_count', info: { last_token_usage: { input_tokens: 1000, cached_input_tokens: 500, output_tokens: 100 } } }
  }
  const content = [context, message, message].map((entry) => JSON.stringify(entry)).join('\n')
  const usage = summarizeTodayJsonl(content, new Date('2026-09-04T12:00:00Z'))
  assert.equal(usage.totalTokens, 1100)
  assert.equal(usage.input, 1000)
  assert.equal(usage.output, 100)
  assert.ok(usage.estimatedCost > 0)
})

test('parses the latest local Codex rate-limit snapshot', () => {
  const content = JSON.stringify({
    type: 'event_msg',
    timestamp: '2030-01-01T00:00:00Z',
    payload: { type: 'token_count', rate_limits: {
      primary: { window_minutes: 300, used_percent: 12, resets_in_seconds: 1800 },
      secondary: { window_minutes: 10080, used_percent: 55, resets_in_seconds: 3600 }
    } }
  })
  const limits = parseLocalRateLimits(content, Date.parse('2030-01-01T00:00:00Z'))
  assert.equal(limits[0].label, '5h')
  assert.equal(limits[0].remainingPercent, 88)
  assert.equal(limits[1].label, '7d')
  assert.equal(limits[1].remainingPercent, 45)
})

test('clamps malformed percentages and ignores irrelevant records', () => {
  const limits = parseRateLimits({ rateLimit: {
    arbitrary: { usedPercent: 150 },
    invalid: { note: 'nothing useful' }
  } })
  assert.equal(limits.length, 1)
  assert.equal(limits[0].remainingPercent, 0)
})

test('maps both DPI-virtualized and physical taskbar coordinates to Electron DIP', () => {
  const display = { x: 0, y: 0, width: 1707, height: 1067 }
  const virtualized = {
    taskbar: { left: 0, top: 1019, right: 1707, bottom: 1067 },
    notification: { left: 1387, top: 1019, right: 1707, bottom: 1067 }
  }
  const physical = {
    taskbar: { left: 0, top: 1528, right: 2560, bottom: 1600 },
    notification: { left: 2080, top: 1528, right: 2560, bottom: 1600 }
  }
  assert.deepEqual(mapTaskbarLayoutToDisplay(virtualized, display), {
    notificationLeft: 1387,
    taskbarHeight: 48,
    isTop: false
  })
  assert.deepEqual(mapTaskbarLayoutToDisplay(physical, display), {
    notificationLeft: 1387,
    taskbarHeight: 48,
    isTop: false
  })
})
