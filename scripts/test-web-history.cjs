'use strict';
const {chromium}=require(process.env.PLAYWRIGHT_MODULE || 'C:/Users/CODER/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const fs=require('node:fs'),assert=require('node:assert/strict');
(async()=>{
 const browser=await chromium.launch({headless:true,channel:'msedge'});
 try {
  const page=await browser.newPage({viewport:{width:1280,height:900}}), errors=[];
  page.on('pageerror',e=>errors.push(e.message));
  const url=fs.readFileSync('artifacts/tests/web-test-url.txt','utf8').trim();
  await page.goto(url+'#overview');
  await page.waitForFunction(()=>document.querySelectorAll('#quota-history svg path').length>0);
  assert.match(await page.locator('#quota-forecast').innerText(),/12\.5% \/ 小时/);
  assert.match(await page.locator('#quota-history').innerText(),/5 小时额度/);
  await page.getByRole('button',{name:'30 天',exact:true}).click();
  await page.waitForFunction(()=>document.querySelector('[data-quota-range="30d"]')?.classList.contains('active')&&document.querySelectorAll('#quota-history svg path').length>0);
  await page.screenshot({path:'artifacts/tests/dashboard/web-quota-history.png',fullPage:true});
  await page.setViewportSize({width:390,height:844});
  assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1));
  assert.deepEqual(errors,[]);
  console.log('Quota history browser tests passed: chart rendering, range selection, source-isolated fixture and responsive layout.');
 } finally {await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
