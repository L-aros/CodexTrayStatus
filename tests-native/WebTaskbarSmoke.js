'use strict';
const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict'),path=require('node:path');
const source=fs.readFileSync(path.join(__dirname,'../native/Statistics/app.js'),'utf8');
const html=fs.readFileSync(path.join(__dirname,'../native/Statistics/index.html'),'utf8');
const elements=new Map();
const $=id=>{if(!elements.has(id))elements.set(id,{value:'',checked:false,style:{},textContent:''});return elements.get(id);};
assert.match(html,/id="taskbar-display-mode"/);
assert.match(html,/<option value="tray">/);
assert.match(html,/<option value="overlay">/);
const context={$,location:{hash:'#settings'},preferences:{Revision:3},settingsDirty:true,previewSequence:0,
  fillTaskbarWindows(){},readReminderSettings(){return {};},formBaseline:{Reminders:{},TaskbarDisplay:{DisplayMode:'overlay',SelectionMode:'default',SelectedWindowKey:'',ShowCost:true,ShowCountdown:true}},
  api:async()=>({Revision:3,Status:'fresh',Percent:'72%',Prefix:'剩余 ',Secondary:'今日 12K',Detail:'在线获取',Accent:'green'})};
vm.createContext(context);
vm.runInContext(source.slice(source.indexOf('function readTaskbarSettings()'),source.indexOf('const reminderChecks=')),context);
$('taskbar-display-mode').value='overlay';$('taskbar-mode').value='default';$('taskbar-cost').checked=true;$('taskbar-countdown').checked=true;
$('remaining').value='true';$('interval').value='60';$('settings-currency').value='USD';$('exchange').value='7';
assert.equal(vm.runInContext('readTaskbarSettings().DisplayMode',context),'overlay');
assert.equal(vm.runInContext('formPatch().TaskbarDisplay',context),undefined,'Unchanged mode is not spuriously saved');
$('taskbar-display-mode').value='tray';
assert.equal(vm.runInContext('formPatch().TaskbarDisplay.DisplayMode',context),'tray');
(async()=>{
  await vm.runInContext('taskbarPreview()',context);
  assert.equal($('taskbar-preview-percent').textContent,'72');
  assert.match($('taskbar-preview-secondary').textContent,/剩余/);
  context.api=async()=>({Revision:3,Status:'stale',Percent:'72%',Detail:'旧值',Accent:'neutral'});
  await vm.runInContext('taskbarPreview()',context);
  assert.equal($('taskbar-preview-percent').textContent,'?','Stale preview matches native unknown icon');
  $('taskbar-display-mode').value='overlay';
  context.api=async()=>({Revision:3,Status:'fresh',Percent:'28%',Prefix:'已用 ',Secondary:'今日 12K',Detail:'在线获取',Accent:'green'});
  await vm.runInContext('taskbarPreview()',context);
  assert.equal($('taskbar-preview-percent').textContent,'28%');
  assert.match($('taskbar-preview-status').textContent,/可能与任务栏按钮重叠/);
  console.log('Web taskbar display mode, partial patches and preview tests passed.');
})().catch(error=>{console.error(error);process.exitCode=1;});
