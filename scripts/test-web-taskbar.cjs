'use strict';
const {chromium}=require(process.env.PLAYWRIGHT_MODULE || 'C:/Users/CODER/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const fs=require('node:fs'),assert=require('node:assert/strict');
(async()=>{
 const browser=await chromium.launch({headless:true,channel:'msedge'});
 try {
  const page=await browser.newPage({viewport:{width:1440,height:1080}}), errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  const url=fs.readFileSync('artifacts/tests/web-test-url.txt','utf8').trim();
  await page.goto(url+'#settings');await page.waitForFunction(()=>preferences?.TaskbarDisplay && formBaseline);
  await page.evaluate(async()=>{await savePreferences({TaskbarDisplay:{SelectionMode:'default',SelectedWindowKey:'',ShowCost:true,ShowCountdown:true},Currency:'CNY',ExchangeRate:7.25,Reminders:{Enabled:false}});});
  assert.equal(await page.locator('#taskbar-cost').isChecked(),true);
  await page.locator('#taskbar-mode').selectOption('selected');
  await page.locator('#taskbar-window').waitFor({state:'visible'});
  await page.waitForFunction(()=>!document.querySelector('#taskbar-window').disabled);
  await page.locator('#taskbar-window').selectOption('fixture-week');
  await page.locator('#taskbar-cost').uncheck();await page.locator('#taskbar-countdown').uncheck();
  await page.waitForFunction(()=>document.querySelector('#taskbar-preview-status').textContent.startsWith('未保存预览'));
  assert.equal(await page.evaluate(()=>preferences.TaskbarDisplay.SelectionMode),'default');
  // Another entry changes unrelated preferences while this form has a draft.
  await page.evaluate(async()=>{await api('preferences',{Reminders:{Enabled:true},Currency:'USD'});await load();});
  assert.equal(await page.locator('#taskbar-cost').isChecked(),false);
  await page.getByRole('button',{name:'保存设置',exact:true}).click();
  await page.waitForFunction(()=>preferences.TaskbarDisplay.SelectionMode==='selected');
  assert.equal(await page.evaluate(()=>preferences.Currency),'USD');
  assert.equal(await page.evaluate(()=>preferences.Reminders.Enabled),true);
  assert.match(await page.locator('#taskbar-preview-prefix').innerText(),/7D/);
  assert.doesNotMatch(await page.locator('#taskbar-preview-secondary').innerText(),/[$¥]/);
  await page.reload();await page.waitForFunction(()=>preferences?.TaskbarDisplay?.SelectedWindowKey==='fixture-week');
  assert.equal(await page.locator('#taskbar-countdown').isChecked(),false);
  // Missing stable selection must stay selectable and restore from persisted settings.
  await page.evaluate(()=>savePreferences({TaskbarDisplay:{SelectedWindowKey:'disappeared'}}));
  assert.match(await page.locator('#taskbar-preview-status').innerText(),/所选窗口暂无数据/);
  assert.equal(await page.locator('#taskbar-window').inputValue(),'disappeared');
  await page.evaluate(()=>savePreferences({TaskbarDisplay:{SelectionMode:'minimum'}}));
  assert.equal(await page.locator('#taskbar-preview-percent').innerText(),'--'); // legacy fixture has unknown timestamps
  // Synthetic fresh fixture for automatic mode; no account access.
  const snapshot={Quota:[{WindowKey:'one',Id:'a',Label:'5h',UsedPercent:30,RemainingPercent:70,ResetsAt:Date.now()+3600000},{WindowKey:'two',Id:'b',Label:'7d',UsedPercent:90,RemainingPercent:10,ResetsAt:Date.now()+3600000}]};
  // Preview comes from the native presenter. API tests cover exact selection; browser checks response handling.
  await page.route('**/api/taskbar-preview',async route=>{
    const response=await route.fetch(),json=await response.json();
    json.Prefix='7D · 剩余 ';json.Percent='10%';json.Status='fresh';json.Accent='red';json.WindowKey=snapshot.Quota[1].WindowKey;
    await route.fulfill({response,json});
  });
  await page.evaluate(()=>taskbarPreview());assert.equal(await page.locator('#taskbar-preview-percent').innerText(),'10%');
  await page.unroute('**/api/taskbar-preview');
  // Save failure keeps draft and committed settings distinct.
  await page.locator('#taskbar-cost').check();
  await page.route('**/api/preferences',async route=>route.request().method()==='POST'?route.fulfill({status:400,body:'模拟保存失败'}):route.continue());
  await page.getByRole('button',{name:'保存设置',exact:true}).click();
  await page.waitForFunction(()=>document.querySelector('#save-status').textContent.includes('模拟保存失败'));
  assert.equal(await page.locator('#taskbar-cost').isChecked(),true);
  assert.equal(await page.evaluate(()=>preferences.TaskbarDisplay.ShowCost),false);
  await page.unroute('**/api/preferences');
  // Reject old GET responses after a newer save.
  await page.evaluate(()=>{const old={...preferences,Revision:preferences.Revision-1,Currency:'CNY'};acceptPreferences(old);});
  assert.equal(await page.evaluate(()=>preferences.Currency),'USD');
  await page.screenshot({path:'artifacts/tests/dashboard/web-taskbar.png',fullPage:true});
  for(const viewport of [{width:960,height:720},{width:390,height:844}]) {
    await page.setViewportSize(viewport);
    assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
  }
  await page.screenshot({path:'artifacts/tests/dashboard/web-taskbar-mobile.png',fullPage:true});
  assert.deepEqual(errors,[]);
  console.log('Taskbar browser tests passed: draft/applied preview, selection, disappearance, unknown, toggles, reload, unrelated settings, save failure, response ordering and responsive layout.');
 } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
