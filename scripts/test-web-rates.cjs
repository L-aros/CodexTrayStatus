'use strict';

const fs=require('node:fs'),path=require('node:path'),assert=require('node:assert/strict');
// Resolve a local installation or the runtime module supplied by the caller.
// Codex's load_workspace_dependencies tool reports the bundled module directory.
const {chromium}=require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const root=path.resolve(__dirname,'..');

(async()=>{
  const browser=await chromium.launch({headless:true,channel:'msedge'});
  try {
    const page=await browser.newPage({viewport:{width:1440,height:1080}}),errors=[],saved=[];
    page.on('pageerror',error=>errors.push(error.message));
    page.on('request',request=>{
      if(request.url().endsWith('/api/preferences')&&request.method()==='POST')saved.push(request.postDataJSON());
    });
    const url=fs.readFileSync(path.join(root,'artifacts/tests/web-test-url.txt'),'utf8').trim();
    assert.match(url,/^http:\/\/127\.0\.0\.1:/,'This suite requires the synthetic local fixture');
    await page.goto(url+'#settings');
    await page.waitForFunction(()=>preferences?.TaskbarDisplay&&formBaseline);
    // Seed only the synthetic server. This fixture deliberately does not contact
    // real rate services or read account credentials.
    await page.evaluate(()=>savePreferences({Currency:'CNY',AutoExchangeRate:true,ExchangeRate:6.6483}));
    assert.equal(await page.locator('#exchange-auto').isChecked(),true);
    assert.equal(await page.locator('#exchange').isDisabled(),true);
    assert.match(await page.locator('#exchange-source-status').innerText(),/参考日期 2026-11-02.*来源：European Central Bank/);
    assert.match(await page.locator('#fx-caption').innerText(),/在线参考汇率/);

    await page.locator('#exchange-auto').uncheck();
    assert.equal(await page.locator('#exchange').isEnabled(),true);
    await page.locator('#exchange').fill('7.25');
    await page.getByRole('button',{name:'保存设置',exact:true}).click();
    await page.waitForFunction(()=>preferences.AutoExchangeRate===false&&preferences.ExchangeRate===7.25&&!settingsDirty);
    assert.equal(saved.at(-1).AutoExchangeRate,false);
    assert.equal(saved.at(-1).ExchangeRate,7.25);
    await page.reload();
    await page.waitForFunction(()=>formBaseline&&preferences.AutoExchangeRate===false);
    assert.equal(await page.locator('#exchange-auto').isChecked(),false);
    assert.equal(await page.locator('#exchange').inputValue(),'7.25');
    assert.equal(await page.locator('#exchange').isEnabled(),true);
    assert.match(await page.locator('#fx-caption').innerText(),/手动参考汇率/);

    await page.locator('#exchange-auto').check();
    await page.getByRole('button',{name:'保存设置',exact:true}).click();
    await page.waitForFunction(()=>preferences.AutoExchangeRate===true&&!settingsDirty);
    assert.equal(saved.at(-1).AutoExchangeRate,true);
    assert.equal(Object.hasOwn(saved.at(-1),'ExchangeRate'),false,'Returning to automatic mode must not post the old manual rate');
    await page.reload();
    await page.waitForFunction(()=>formBaseline&&preferences.AutoExchangeRate===true);
    assert.equal(await page.locator('#exchange').isDisabled(),true);

    // Simulate an online effective rate change through this synthetic fixture.
    await page.evaluate(()=>savePreferences({Currency:'CNY',AutoExchangeRate:true,ExchangeRate:6.6483}));
    await page.locator('#exchange-auto').uncheck();
    assert.equal(await page.locator('#exchange').inputValue(),'6.6483');
    await page.evaluate(async()=>{await api('preferences',{ExchangeRate:6.75});await load();});
    await page.waitForFunction(()=>preferences.ExchangeRate===6.75);
    assert.equal(await page.locator('#exchange-auto').isChecked(),false,'Polling must preserve the unsaved manual-mode draft');
    assert.equal(await page.locator('#exchange').inputValue(),'6.6483','Polling must preserve the visible rate when switching to manual');
    await page.locator('#currency').selectOption('USD');
    await page.waitForFunction(()=>preferences.Currency==='USD');
    assert.equal(await page.locator('#settings-currency').inputValue(),'CNY','A quick currency change must preserve the rest of the draft');
    assert.equal(await page.locator('#exchange').inputValue(),'6.6483');
    await page.getByRole('button',{name:'保存设置',exact:true}).click();
    await page.waitForFunction(()=>preferences.AutoExchangeRate===false&&!settingsDirty);
    assert.equal(saved.at(-1).ExchangeRate,6.6483,'The visible manual rate must be pinned despite a newer automatic rate');
    assert.equal(Object.hasOwn(saved.at(-1),'Currency'),false,'Saving a rate draft must preserve the newer quick currency selection');
    assert.equal(await page.evaluate(()=>preferences.ExchangeRate),6.6483);
    assert.equal(await page.evaluate(()=>preferences.Currency),'USD');

    await page.locator('#exchange').fill('7.35');
    await page.evaluate(async()=>{await api('preferences',{RefreshInterval:300});await load();});
    assert.equal(await page.locator('#exchange').inputValue(),'7.35','Polling must preserve an edited manual value');
    await page.getByRole('button',{name:'保存设置',exact:true}).click();
    await page.waitForFunction(()=>preferences.ExchangeRate===7.35&&!settingsDirty);
    assert.equal(await page.evaluate(()=>preferences.RefreshInterval),300,'Saving only the exchange rate must preserve concurrent unrelated updates');

    const refresh=page.waitForResponse(response=>response.url().endsWith('/api/rates/refresh')&&response.request().method()==='POST');
    await page.getByRole('button',{name:'立即更新汇率与费率',exact:true}).click();
    const response=await refresh;
    assert.equal(response.status(),200);
    assert.deepEqual(response.request().postDataJSON(),{});
    await page.waitForFunction(()=>document.querySelector('#rates-refresh-status').textContent.includes('已请求在线更新'));
    await page.waitForFunction(()=>!document.querySelector('#rates-refresh').disabled);
    fs.mkdirSync(path.join(root,'artifacts/tests/dashboard'),{recursive:true});
    await page.screenshot({path:path.join(root,'artifacts/tests/dashboard/web-rates-settings.png'),fullPage:true});
    assert.deepEqual(errors,[]);
    console.log('Rate browser tests passed: automatic input, manual/automatic save and reload, no stale rate submission, concurrent rate/currency changes, unsaved draft protection and actual refresh endpoint.');
  } finally {await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
