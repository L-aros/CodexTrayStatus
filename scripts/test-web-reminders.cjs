'use strict';
const {chromium}=require(process.env.PLAYWRIGHT_MODULE || 'C:/Users/CODER/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const fs=require('node:fs');
const assert=require('node:assert/strict');
(async()=>{
 const browser=await chromium.launch({headless:true,channel:'msedge'});
 try {
  const page=await browser.newPage({viewport:{width:1440,height:1080}});
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  const url=fs.readFileSync('artifacts/tests/web-test-url.txt','utf8').trim();
  await page.goto(url+'#settings');
  await page.waitForFunction(()=>preferences?.Reminders);
  assert.equal(await page.locator('#reminder-enabled').isChecked(),true);
  assert.equal(await page.locator('#reminder-start').inputValue(),'23:00');
  assert.equal(await page.locator('#reminder-end').inputValue(),'08:00');
  await page.locator('#reminder-10').uncheck();
  await page.locator('#reminder-quiet').check();
  await page.locator('#reminder-start').fill('22:30');
  await page.locator('#reminder-end').fill('07:45');
  await page.locator('#reminder-snooze').selectOption('hour');
  await page.getByRole('button',{name:'保存设置',exact:true}).click();
  await page.waitForFunction(()=>document.querySelector('#save-status').textContent.startsWith('已保存'));
  const until=await page.evaluate(()=>preferences.Reminders.SnoozeUntilUtcMs);
  assert.ok(until>Date.now()+3500000);
  await page.reload();await page.waitForFunction(()=>preferences?.Reminders?.QuietStartMinutes===1350);
  assert.equal(await page.locator('#reminder-10').isChecked(),false);
  assert.equal(await page.locator('#reminder-20').isChecked(),true);
  assert.equal(await page.locator('#reminder-end').inputValue(),'07:45');
  assert.ok((await page.locator('#reminder-status').innerText()).includes('暂停至'));
  // Legacy client and partial patches must preserve the stored quiet schedule.
  await page.evaluate(async()=>{const {Reminders,...old}=preferences;await savePreferences(old);await savePreferences({...old,Reminders:{Threshold0:false}});});
  assert.equal(await page.evaluate(()=>preferences.Reminders.QuietStartMinutes),1350);
  assert.equal(await page.evaluate(()=>preferences.Reminders.SnoozeUntilUtcMs),until);
  await page.locator('#reminder-end').fill('22:30');
  await page.getByRole('button',{name:'保存设置',exact:true}).click();
  await page.waitForFunction(()=>document.querySelector('#save-status').textContent.includes('开始和结束时间必须不同'));
  assert.equal(await page.evaluate(()=>preferences.Reminders.QuietEndMinutes),465);
  // Polls must not replace unsaved form edits.
  await page.evaluate(()=>load());assert.equal(await page.locator('#reminder-end').inputValue(),'22:30');
  await page.locator('#reminder-end').fill('07:45');
  await page.locator('#reminder-snooze').selectOption('resume');
  await page.getByRole('button',{name:'保存设置',exact:true}).click();
  await page.waitForFunction(()=>preferences.Reminders.SnoozeUntilUtcMs===0);
  await page.locator('#reminder-enabled').uncheck();
  await page.getByRole('button',{name:'保存设置',exact:true}).click();
  await page.waitForFunction(()=>preferences.Reminders.Enabled===false);
  assert.ok((await page.locator('#reminder-status').innerText()).includes('已关闭'));
  await page.screenshot({path:'artifacts/tests/dashboard/web-reminders.png',fullPage:true});
  // Synthetic response failure status; no production store or notification access.
  await page.route('**/api/usage',async route=>{const response=await route.fetch();const json=await response.json();json.ReminderStatus={StorageAvailable:false,ErrorCode:'state_read_failed'};await route.fulfill({response,json});});
  await page.evaluate(()=>load());await page.locator('#reminder-rebuild').waitFor({state:'visible'});
  assert.ok((await page.locator('#reminder-status').innerText()).includes('无法读取'));
  await page.locator('#reminder-rebuild').click();
  await page.waitForFunction(()=>document.querySelector('#reminder-action-status').textContent.includes('已重建'));
  await page.evaluate(()=>{connected=false;reminderStatus();});
  assert.ok((await page.locator('#reminder-status').innerText()).includes('连接中断'));
  await page.setViewportSize({width:390,height:844});
  assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'reminder settings mobile overflow');
  await page.screenshot({path:'artifacts/tests/dashboard/web-reminders-mobile.png',fullPage:true});
  assert.deepEqual(errors,[]);
  console.log('Reminder browser tests passed: defaults, saved nested settings, legacy compatibility, snooze, invalid schedule, dirty form, failure/rebuild status, mobile.');
 } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
