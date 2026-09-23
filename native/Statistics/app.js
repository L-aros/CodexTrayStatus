'use strict';
const $ = id => document.getElementById(id);
const fields = ['Input','CachedInput','UncachedInput','Output','TotalTokens','EstimatedCost','CachedInputCost','UncachedInputCost','OutputCost','UnpricedTokens'];
let data = null, preferences = null, range = 7, selectedRows = [], settingsDirty = false, loading = false, quotaHistory = null, quotaRange = '7d';
let connected = true;
let formBaseline = null, previewSequence = 0, previewTimer = null;
const dismissedNotices = new Set();
for (const id of ['freshness','reminder-status']) {
  try { if(sessionStorage.getItem('dismissed-'+id)==='1') dismissedNotices.add(id); } catch (_) {}
  $(id).hidden=dismissedNotices.has(id);
}
document.querySelectorAll('[data-dismiss]').forEach(button=>button.addEventListener('click',()=>{
  const id=button.dataset.dismiss;
  dismissedNotices.add(id);
  $(id).hidden=true;
  try { sessionStorage.setItem('dismissed-'+id,'1'); } catch (_) {}
}));
function acceptPreferences(next) {
  if(!preferences || (next.Revision || 0) >= (preferences.Revision || 0)) preferences=next;
}
const stamp = value => value ? new Date(value).toLocaleString('zh-CN') : '未知';
const failureText = code => ({official_timeout:'在线请求超时',official_failed:'在线获取失败',quota_failed:'额度获取失败',usage_scan_failed:'日志扫描失败',refresh_failed:'刷新失败',unknown_metadata:'旧数据时间未知'}[code] || '数据暂不可用');
function stateLabel(state, validity) {
  let label = !state?(validity?.HasValue?'来源未知':'暂无数据'):state.Delivery==='unavailable'?'暂无数据':state.Delivery==='retained'?'保留旧值':state.Source==='official'?'在线获取':state.Scope==='local_logs'?'统计成功':'本地回退';
  if(validity?.HasValue && (!validity.IsFresh || (validity.ExpiresAt && Date.now()>=validity.ExpiresAt)))label+=' · 已过期或时间未知';
  if(state?.Scope==='unattributed_local_quota' || (state?.Scope==='account'&&!state.AccountKey))label+=' · 账号未知';
  return label;
}
function freshness() {
  if(!data)return;
  const q=data.QuotaState,u=data.UsageState,r=data.RefreshState;
  $('freshness-text').textContent=(connected?'':'托盘连接中断 · ')+
    '额度：'+stateLabel(q,data.QuotaValidity?.[0])+'；数据时间 '+stamp(q?.ObservedAt)+'；在线成功 '+stamp(q?.LastOfficialSuccessAt)+
    '。日志：'+stateLabel(u,data.UsageValidity)+'；完整统计 '+stamp(u?.LastSuccessAt)+
    '。最近尝试 '+stamp(r?.AttemptedAt)+(r?.IsRefreshing?' · 刷新中':'')+
    (q?.ErrorCode?'；额度：'+failureText(q.ErrorCode):'')+(u?.ErrorCode?'；日志：'+failureText(u.ErrorCode):'');
  document.querySelectorAll('.updated').forEach(el=>el.textContent='最近尝试 '+stamp(r?.AttemptedAt));
}
const num = value => Number(value || 0);
const exact = value => new Intl.NumberFormat('zh-CN', {maximumFractionDigits:0}).format(num(value));
const short = value => num(value) >= 1e6 ? (num(value)/1e6).toFixed(2)+'M' : num(value) >= 1e3 ? (num(value)/1e3).toFixed(1)+'K' : exact(value);
const escapeHtml = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const empty = () => Object.fromEntries(fields.map(f => [f,0]));
function sum(rows) { return rows.reduce((a,row) => {fields.forEach(f => a[f] += num(row[f])); return a;}, empty()); }
function money(value, detail=false) { return new Intl.NumberFormat('zh-CN',{style:'currency',currencyDisplay:'narrowSymbol',currency:preferences?.Currency || 'USD',minimumFractionDigits:detail?4:2,maximumFractionDigits:detail?4:2}).format(num(value)*(preferences?.Currency==='CNY'?preferences.ExchangeRate:1)); }
function cost(row, detail=false) { return (row.UnpricedTokens ? '≥ ' : '') + money(row.EstimatedCost,detail); }
function dateOffset(date, days) { const d=new Date(date+'T12:00:00'); d.setDate(d.getDate()+days); return `${d.getFullYear()}-${String(d.getMonth()+1).padStart(2,'0')}-${String(d.getDate()).padStart(2,'0')}`; }
function today() { return dateOffset(new Date().toLocaleDateString('sv-SE'),0); }
function byModel(rows) {
  const models = new Map();
  rows.flatMap(row=>row.Models || []).forEach(row=>{
    if (!models.has(row.Model)) models.set(row.Model,{...empty(),Model:row.Model,InputRate:row.InputRate,CachedRate:row.CachedRate,OutputRate:row.OutputRate});
    const dest=models.get(row.Model); fields.forEach(f=>dest[f]+=num(row[f]));
  });
  return [...models.values()].sort((a,b)=>b.EstimatedCost-a.EstimatedCost || b.TotalTokens-a.TotalTokens);
}
function modelTable(rows) {
  rows=[...rows].sort((a,b)=>b.EstimatedCost-a.EstimatedCost || b.TotalTokens-a.TotalTokens);
  if (!rows.length) return '<div class="empty">此范围暂无模型用量记录。</div>';
  return `<table><thead><tr><th>模型</th><th>未缓存输入</th><th>缓存输入</th><th>输出</th><th>总 Token</th><th>估算费用</th></tr></thead><tbody>${rows.map(row=>`<tr><td class="model-name">${escapeHtml(row.Model)}<small>${row.UnpricedTokens?'包含待定价用量':row.Model==='codex-auto-review'?'按 GPT-5.4 标准价':'标准价 / 每 1M Token'}</small></td>${['UncachedInput','CachedInput','Output'].map((key,i)=>`<td>${exact(row[key])}<small>${row.UnpricedTokens?'待定价':money(row[['UncachedInputCost','CachedInputCost','OutputCost'][i]],true)} · ${row.UnpricedTokens?'单价未知':money(row[['InputRate','CachedRate','OutputRate'][i]],true)+'/M'}</small></td>`).join('')}<td>${exact(row.TotalTokens)}<small>缓存占输入 ${row.Input?(row.CachedInput/row.Input*100).toFixed(1):'0'}%</small></td><td class="price">${row.UnpricedTokens===row.TotalTokens?'待定价':cost(row,true)}</td></tr>`).join('')}</tbody></table>`;
}
function cards(id,total) {
  const entries=[['总 Token',short(total.TotalTokens),exact(total.TotalTokens)+' Tokens',''],['未缓存输入',short(total.UncachedInput),money(total.UncachedInputCost,true),'uncached'],['缓存输入',short(total.CachedInput),money(total.CachedInputCost,true)+' · 命中率 '+(total.Input?(total.CachedInput/total.Input*100).toFixed(1):'0')+'%','cached'],['输出',short(total.Output),money(total.OutputCost,true),'output'],['估算费用',cost(total),total.UnpricedTokens?exact(total.UnpricedTokens)+' Tokens 待定价':'按模型标准单价折算','cost']];
  $(id).innerHTML=entries.map(([label,value,detail,cls])=>`<article class="metric ${cls}"><div class="metric-label">${label}</div><div class="metric-value" title="${escapeHtml(detail)}">${value}</div><div class="metric-detail">${detail}</div></article>`).join('');
  if(!data?.Daily)$(id).querySelectorAll('.metric').forEach(el=>{el.querySelector('.metric-value').textContent='--';el.querySelector('.metric-detail').textContent='等待统计数据';});
}
function chart(id,rows) {
  if(!rows.length){$(id).innerHTML='<div class="empty">请选择有效的日期范围。</div>';return;}
  const peak=Math.max(1,...rows.map(r=>r.TotalTokens));
  $(id).innerHTML=`<div class="chart-plot" role="img" aria-label="每日 Token 组成柱状图">${rows.map(row=>{
    const label=`${row.DateLabel}：未缓存 ${exact(row.UncachedInput)}，缓存 ${exact(row.CachedInput)}，输出 ${exact(row.Output)}，费用 ${cost(row,true)}`;
    return `<div class="bar-wrap"><button class="bar" aria-label="${escapeHtml(label)}" title="${escapeHtml(label)}" style="height:${Math.max(0.5,row.TotalTokens/peak*100)}%">${['UncachedInput','CachedInput','Output'].map((key,i)=>`<span class="${['uncached','cached','output'][i]}" style="height:${row.TotalTokens?row[key]/row.TotalTokens*100:0}%"></span>`).join('')}</button></div>`;
  }).join('')}</div><div class="chart-axis"><span>${rows[0].DateLabel}</span><span>峰值 ${short(peak===1?0:peak)} Tokens</span><span>${rows[rows.length-1].DateLabel}</span></div>`;
}
function quotas() {
  const windows=data?.Quota || [];
  if(!windows.length){$('quota').innerHTML='<div class="quota-card"><h2>额度暂不可用</h2><p class="subtitle">等待 Codex 额度刷新</p></div>';return;}
  $('quota').innerHTML=windows.map((w,index)=>{
    const value=preferences?.ShowRemaining?w.RemainingPercent:w.UsedPercent;
    const remaining=w.ResetsAt?Math.max(0,w.ResetsAt-Date.now()):null;
    const reset=stateLabel(data.QuotaState,data.QuotaValidity?.[index])+' · '+(remaining===null?'重置时间未知':remaining===0?'窗口已到期，等待刷新':`${Math.floor(remaining/86400000)}天 ${Math.floor(remaining%86400000/3600000)}小时 ${Math.floor(remaining%3600000/60000)}分钟后重置`);
    return `<article class="quota-card"><div class="quota-title"><strong>${escapeHtml(w.Label==='5h'?'5 小时额度':w.Label==='7d'?'每周额度':w.Label)}</strong><span>${preferences?.ShowRemaining?'剩余':'已使用'}</span></div><div class="quota-number">${value==null?'--':Math.round(value)+'%'}</div><div class="track"><span style="width:${Math.max(0,Math.min(100,num(value)))}%"></span></div><div class="quota-reset">${reset}${w.ResetsAt?' · '+new Date(w.ResetsAt).toLocaleString('zh-CN'):''}</div></article>`;
  }).join('');
}
function quotaWindowName(key) { const window=(data?.Quota||[]).find(item=>item.WindowKey===key); return window?.Label==='5h'?'5 小时额度':window?.Label==='7d'?'每周额度':window?.Label||key||'额度'; }
function quotaHistoryLabel(point) { return `${quotaWindowName(point.WindowKey)} · ${new Date(point.ObservedAt).toLocaleString('zh-CN')} · 剩余 ${Number(point.RemainingPercent).toFixed(1)}%`; }
function renderQuotaHistory() {
  const target=$('quota-history'), caption=$('quota-history-caption');
  if(!quotaHistory){target.innerHTML='<div class="history-empty">正在读取额度历史…</div>';return;}
  if(!quotaHistory.Available){target.innerHTML='<div class="history-empty">额度历史暂不可用，请等待下一次在线额度更新。</div>';caption.textContent='历史文件暂不可用；当前额度不会受影响。';return;}
  const points=(quotaHistory.Points||[]).filter(p=>Number.isFinite(Number(p.UsedPercent))&&Number.isFinite(Number(p.RemainingPercent))).sort((a,b)=>a.ObservedAt-b.ObservedAt);
  if(!points.length){target.innerHTML='<div class="history-empty">此范围还没有确认的在线额度观测。历史从升级后首次成功获取开始记录。</div>';caption.textContent='仅记录确认的在线额度观测；缺测不会补线。';return;}
  const from=Number(quotaHistory.From),to=Math.max(from+1,Number(quotaHistory.To)), width=760,height=190,left=36,right=12,top=12,bottom=27;
  const x=value=>left+(Number(value)-from)/(to-from)*(width-left-right), y=value=>top+(100-Number(value))/100*(height-top-bottom);
  const byWindow=new Map();points.forEach(p=>{const key=p.WindowKey||'unknown';if(!byWindow.has(key))byWindow.set(key,[]);byWindow.get(key).push(p);});
  const colors=['#53c97e','#8fb7fc','#bb9bfa','#e6ad3e'];let lineIndex=0,markup='';
  for(const [key,rows] of byWindow){const color=colors[lineIndex++%colors.length];let previous=null,path='',move=true;for(const point of rows){const changed=previous&&Math.abs(Number(point.CycleEnd)-Number(previous.CycleEnd))>60000;if(changed){markup+=`<line class="history-reset" x1="${x(point.ObservedAt).toFixed(1)}" y1="${top}" x2="${x(point.ObservedAt).toFixed(1)}" y2="${height-bottom}"/>`;move=true;}path+=`${move?'M':'L'}${x(point.ObservedAt).toFixed(1)},${y(point.UsedPercent).toFixed(1)} `;move=false;previous=point;}markup+=`<path class="history-line" stroke="${color}" d="${path}"/>`;rows.forEach(point=>{markup+=`<circle class="history-dot" fill="${color}" cx="${x(point.ObservedAt).toFixed(1)}" cy="${y(point.UsedPercent).toFixed(1)}" r="3"><title>${escapeHtml(quotaHistoryLabel(point))}</title></circle>`;});markup+=`<text class="history-key" x="${left+(lineIndex-1)*135}" y="${height-7}" fill="${color}">${escapeHtml(quotaWindowName(key))}</text>`;}
  for(const value of [0,50,100])markup+=`<line class="history-grid" x1="${left}" y1="${y(value)}" x2="${width-right}" y2="${y(value)}"/><text class="history-key" x="2" y="${y(value)+4}">${value}%</text>`;
  target.innerHTML=`<svg viewBox="0 0 ${width} ${height}" aria-label="${escapeHtml(quotaRange)}额度已用百分比历史">${markup}<text class="history-key" x="${left}" y="${height-7}">${new Date(from).toLocaleDateString('zh-CN')}</text><text class="history-key" text-anchor="end" x="${width-right}" y="${height-7}">${new Date(to).toLocaleDateString('zh-CN')}</text></svg>`;
  caption.textContent=`${points.length} 个确认观测 · 曲线为已用百分比，虚线表示重置周期变化；缺测不会补线。`;
}
async function loadQuotaHistory() { try { quotaHistory=await api(`quota-history?range=${encodeURIComponent(quotaRange)}`);renderQuotaHistory(); } catch { quotaHistory={Available:false};renderQuotaHistory(); } }
function selected() {
  const from=$('from').value,to=$('to').value,model=$('model').value;
  return (data?.Daily || []).filter(row=>row.DateLabel>=from&&row.DateLabel<=to).map(row=>{
    const models=(row.Models||[]).filter(item=>!model||item.Model===model);
    return {...sum(models),DateLabel:row.DateLabel,Models:models};
  });
}
function render() {
  if(!data)return;
  quotas();
  renderQuotaHistory();
  const days=data.Daily || [];
  const day=days.find(row=>row.DateLabel===today());
  if(day)cards('today-cards',day);else $('today-cards').textContent='暂无有效的今日统计';
  chart('overview-chart',days.filter(row=>row.DateLabel>=dateOffset(today(),-6)));
  $('today-models').innerHTML=modelTable(day?.Models || []);
  selectedRows=selected();
  cards('stats-cards',sum(selectedRows)); chart('stats-chart',selectedRows);
  if(!data.Daily)$('stats-cards').textContent='暂无有效统计';
  $('range-caption').textContent=`${$('from').value} — ${$('to').value} · ${selectedRows.length} 天`;
  $('models-table').innerHTML=modelTable(byModel(selectedRows));
  const openDates=new Set([...$('daily-table').querySelectorAll('details[open]')].map(el=>el.dataset.date));
  $('daily-table').innerHTML=selectedRows.length?[...selectedRows].reverse().map(row=>`<details data-date="${row.DateLabel}" ${openDates.has(row.DateLabel)?'open':''}><summary><span class="daily-date">${row.DateLabel}</span><span class="daily-total">${exact(row.TotalTokens)} Tokens · 未缓存 ${exact(row.UncachedInput)} / 缓存 ${exact(row.CachedInput)} / 输出 ${exact(row.Output)}</span><strong>${cost(row,true)}</strong></summary>${modelTable(row.Models)}</details>`).join(''):'<div class="empty">此日期范围暂无记录。</div>';
  const currency=preferences?.Currency || 'USD';
  document.querySelectorAll('.currency-caption').forEach(el=>el.textContent=`费用单位：${currency}`);
  $('fx-caption').textContent=currency==='CNY'?`人民币 · 1 USD = ${preferences.ExchangeRate} CNY（手动参考汇率）`:'美元 USD';
  freshness();reminderStatus();
}
function setRange(days) {
  range=days; $('to').value=today(); $('from').value=dateOffset(today(),1-days);
  document.querySelectorAll('[data-days]').forEach(el=>el.classList.toggle('active',Number(el.dataset.days)===days)); render();
}
function fillSettings() {
  if(!preferences)return;
  $('currency').value=preferences.Currency;
  if(settingsDirty)return;
  $('settings-currency').value=preferences.Currency; $('exchange').value=preferences.ExchangeRate;
  $('interval').value=preferences.RefreshInterval; $('remaining').value=String(preferences.ShowRemaining); $('autostart').checked=preferences.AutoStart;
  const r=preferences.Reminders || {};
  for(const [id,key] of Object.entries(reminderChecks))$(id).checked=r[key] ?? key!=='QuietHoursEnabled';
  $('reminder-start').value=minutesToTime(r.QuietStartMinutes ?? 1380);
  $('reminder-end').value=minutesToTime(r.QuietEndMinutes ?? 480);
  $('reminder-snooze').value='keep';
  const t=preferences.TaskbarDisplay || {SelectionMode:'default',SelectedWindowKey:'',ShowCost:true,ShowCountdown:true};
  $('taskbar-mode').value=t.SelectionMode;$('taskbar-cost').checked=t.ShowCost;$('taskbar-countdown').checked=t.ShowCountdown;
  fillTaskbarWindows(t.SelectedWindowKey);
  formBaseline=JSON.parse(JSON.stringify(preferences));
}
function fillTaskbarWindows(key=$('taskbar-window').value) {
  const windows=(data?.Quota || []).filter(w=>w?.WindowKey);
  const seen=new Set();$('taskbar-window').replaceChildren(new Option('请选择窗口',''));
  for(const w of windows) {
    if(seen.has(w.WindowKey))continue;seen.add(w.WindowKey);
    $('taskbar-window').add(new Option(`${w.Label || '额度'} · ${w.Id || w.WindowKey}`,w.WindowKey));
  }
  if(key&&!seen.has(key))$('taskbar-window').add(new Option('所选窗口暂无数据',key));
  $('taskbar-window').value=key || '';
  $('taskbar-window').disabled=$('taskbar-mode').value!=='selected';
}
function readTaskbarSettings() {
  return {SelectionMode:$('taskbar-mode').value,SelectedWindowKey:$('taskbar-window').value,ShowCost:$('taskbar-cost').checked,ShowCountdown:$('taskbar-countdown').checked};
}
function formPatch() {
  const all={RefreshInterval:Number($('interval').value),ShowRemaining:$('remaining').value==='true',AutoStart:$('autostart').checked,Currency:$('settings-currency').value,ExchangeRate:Number($('exchange').value)};
  const result={};for(const [key,value] of Object.entries(all))if(value!==formBaseline?.[key])result[key]=value;
  for(const [name,values] of [['Reminders',readReminderSettings()],['TaskbarDisplay',readTaskbarSettings()]]) {
    const patch={};for(const [key,value] of Object.entries(values))if(value!==formBaseline?.[name]?.[key])patch[key]=value;
    if(Object.keys(patch).length)result[name]=patch;
  }
  if(!Object.keys(result).length && ['settings_read_failed','settings_write_failed'].includes(data?.ReminderStatus?.ErrorCode))result.Reminders=readReminderSettings();
  return result;
}
async function taskbarPreview() {
  if(!preferences || location.hash!=='#settings')return;
  const sequence=++previewSequence;
  fillTaskbarWindows();
  $('taskbar-settings-error').textContent=preferences.TaskbarSettingsError || '';
  $('taskbar-settings-error').hidden=!preferences.TaskbarSettingsError;
  try {
    const draft=settingsDirty?{TaskbarDisplay:readTaskbarSettings(),ShowRemaining:$('remaining').value==='true',Currency:$('settings-currency').value,ExchangeRate:Number($('exchange').value)}:undefined;
    const view=await api('taskbar-preview',draft);
    if(sequence!==previewSequence || view.Revision<(preferences.Revision || 0))return;
    $('taskbar-preview-prefix').textContent=view.Prefix;$('taskbar-preview-percent').textContent=view.Percent;
    $('taskbar-preview-secondary').textContent=view.Secondary;
    $('taskbar-preview-percent').style.color={red:'#ef5c5c',amber:'#e6ad3e',green:'#53c97e',neutral:'#96a0ae'}[view.Accent];
    $('taskbar-preview-status').textContent=(settingsDirty?'未保存预览':'已应用')+' · '+view.Detail+' · 文字预览，实际布局随任务栏空间调整';
  } catch(error) {if(sequence===previewSequence)$('taskbar-preview-status').textContent='预览暂不可用：'+error.message;}
}
const reminderChecks={'reminder-enabled':'Enabled','reminder-20':'Threshold20','reminder-10':'Threshold10','reminder-0':'Threshold0','reminder-recovery':'RecoveryEnabled','reminder-quiet':'QuietHoursEnabled'};
const minutesToTime=m=>`${String(Math.floor(m/60)).padStart(2,'0')}:${String(m%60).padStart(2,'0')}`;
function readReminderSettings() {
  const next=Object.fromEntries(Object.entries(reminderChecks).map(([id,key])=>[key,$(id).checked]));
  const minutes=id=>{const [h,m]=$(id).value.split(':').map(Number);return h*60+m;};
  next.QuietStartMinutes=minutes('reminder-start');next.QuietEndMinutes=minutes('reminder-end');
  if(!Number.isInteger(next.QuietStartMinutes)||!Number.isInteger(next.QuietEndMinutes)||next.QuietStartMinutes===next.QuietEndMinutes)throw new Error('免打扰开始和结束时间必须不同');
  const choice=$('reminder-snooze').value;
  if(choice==='resume')next.SnoozeUntilUtcMs=0;
  if(choice==='hour')next.SnoozeUntilUtcMs=Date.now()+3600000;
  if(choice==='tomorrow'){const end=new Date();end.setDate(end.getDate()+1);end.setHours(8,0,0,0);next.SnoozeUntilUtcMs=end.getTime();}
  return next;
}
function reminderStatus() {
  const s=data?.ReminderStatus, r=preferences?.Reminders;
  const errors={state_read_failed:'提醒状态无法读取，自动提醒已暂停',state_write_failed:'提醒状态无法保存，自动提醒已暂停',state_evaluation_failed:'提醒状态处理失败，自动提醒已暂停',notification_failed:'上次系统通知调用失败，该提醒不会重复发送',settings_read_failed:'提醒设置无法读取，请重新保存设置',settings_write_failed:'提醒设置保存失败，请重新保存设置'};
  let message=!connected?'连接中断，提醒状态暂无法确认':s?.ErrorCode?errors[s.ErrorCode]||'提醒暂不可用':!r?'等待提醒设置':r.Enabled===false?'额度提醒已关闭':r.SnoozeUntilUtcMs>Date.now()?'额度提醒暂停至 '+stamp(r.SnoozeUntilUtcMs):s?.Suppression==='quiet_hours'?'额度提醒处于定时免打扰':'额度提醒已开启，仅依据有效在线新数据';
  $('reminder-status-text').textContent=message;
  const rebuild=connected&&s?.StorageAvailable===false;
  $('reminder-rebuild').hidden=!rebuild;$('reminder-rebuild-help').hidden=!rebuild;
}
function notice(message) {$('notice').textContent=message;$('notice').hidden=!message;}
async function api(path,body) {
  const response=await fetch('api/'+path,{method:body===undefined?'GET':'POST',headers:body===undefined?{}:{'Content-Type':'application/json'},body:body===undefined?undefined:JSON.stringify(body),cache:'no-store',signal:AbortSignal.timeout(15000)});
  if(!response.ok)throw new Error(await response.text());return response.json();
}
async function load() {
  if(loading)return;loading=true;
  try {
    const [next,prefs,prices]=await Promise.all([api('usage'),api('preferences'),api('pricing')]); const oldToday=data?.LocalDate;data=next;acceptPreferences(prefs);if(document.activeElement!==$('pricing-json'))$('pricing-json').value=Object.keys(prices).length?JSON.stringify(prices,null,2):'';connected=true;
    const model=$('model').value;
    $('model').innerHTML='<option value="">全部模型</option>'+byModel(data.Daily||[]).map(row=>`<option>${escapeHtml(row.Model)}</option>`).join('');
    if([...$('model').options].some(option=>option.value===model))$('model').value=model;
    const first=dateOffset(today(),-89); for(const id of ['from','to']){$(id).min=first;$(id).max=today();}
    if(!$('from').value || (range&&oldToday!==data.LocalDate))setRange(range || 7);
    fillSettings();render();taskbarPreview();loadQuotaHistory();
    notice(data.Error || (data.Daily===null?'正在扫描本地会话，请稍候…':''));
  } catch(error) {connected=false;freshness();reminderStatus();notice('暂时无法连接托盘程序。请确认程序仍在运行；已载入的数据将保留。');}
  finally{loading=false;}
}
async function savePreferences(next) {
  acceptPreferences(await api('preferences',next));settingsDirty=false;fillSettings();render();await taskbarPreview();
}
function navigate() {
  const page=['overview','statistics','settings'].includes(location.hash.slice(1))?location.hash.slice(1):'overview';
  document.querySelectorAll('.page').forEach(el=>el.hidden=el.id!==page);
  document.querySelectorAll('nav a').forEach(el=>el.classList.toggle('active',el.dataset.page===page));
  $('breadcrumb').textContent={overview:'用量概览',statistics:'详细统计',settings:'设置'}[page];
  taskbarPreview();
}
document.querySelectorAll('[data-days]').forEach(el=>el.addEventListener('click',()=>setRange(Number(el.dataset.days))));
document.querySelectorAll('[data-quota-range]').forEach(el=>el.addEventListener('click',()=>{quotaRange=el.dataset.quotaRange;document.querySelectorAll('[data-quota-range]').forEach(item=>item.classList.toggle('active',item===el));quotaHistory=null;renderQuotaHistory();loadQuotaHistory();}));
for(const id of ['from','to'])$(id).addEventListener('change',()=>{
  range=0;document.querySelectorAll('[data-days]').forEach(el=>el.classList.remove('active'));
  const valid=$('from').value<=$('to').value&&$('from').value>=$('from').min&&$('to').value<=$('to').max;
  notice(valid?'':'请选择最近 90 天内的有效日期范围，开始日期不能晚于结束日期。');render();
});
$('model').addEventListener('change',render);
$('pricing-save').addEventListener('click',async()=>{try{const value=JSON.parse($('pricing-json').value||'{}');await api('pricing',value);$('pricing-status').textContent='已保存；正在重新汇总费用。';}catch(error){$('pricing-status').textContent='费率格式或数值无效：'+error.message;}});
$('pricing-reset').addEventListener('click',async()=>{try{await api('pricing',{});$('pricing-json').value='';$('pricing-status').textContent='已恢复内置费率；正在重新汇总费用。';}catch(error){$('pricing-status').textContent='恢复失败：'+error.message;}});
$('settings-form').addEventListener('input',()=>{settingsDirty=true;++previewSequence;$('save-status').textContent='有未保存的修改';clearTimeout(previewTimer);previewTimer=setTimeout(taskbarPreview,200);});
$('settings-form').addEventListener('submit',async event=>{
  event.preventDefault();const button=event.submitter;button.disabled=true;
  try{const patch=formPatch();if(preferences.TaskbarSettingsError)patch.TaskbarDisplay=readTaskbarSettings();await savePreferences(patch);$('save-status').textContent='已保存，网页与任务栏已同步';}
  catch(error){$('save-status').textContent='保存失败：'+error.message;}finally{button.disabled=false;}
});
$('currency').addEventListener('change',async()=>{
  if(!preferences)return;const currency=$('currency').value;$('currency').disabled=true;
  const dirty=settingsDirty;
  try{acceptPreferences(await api('preferences',{Currency:currency}));settingsDirty=dirty;fillSettings();render();taskbarPreview();}catch(error){notice('币种保存失败：'+error.message);fillSettings();}finally{$('currency').disabled=false;}
});
$('reminder-rebuild').addEventListener('click',async()=>{
  $('reminder-rebuild').disabled=true;
  try{await api('reminders/rebuild',{});$('reminder-action-status').textContent='状态已重建，首次在线观测只建立基线';await load();}
  catch(error){$('reminder-action-status').textContent='重建失败：'+error.message;}
  finally{$('reminder-rebuild').disabled=false;}
});
$('refresh').addEventListener('click',async()=>{
  $('refresh').disabled=true;
  try{await api('refresh',{});notice('已请求刷新，完成后会自动同步。');setTimeout(load,1500);}
  catch(error){notice('刷新失败：'+error.message);}finally{$('refresh').disabled=false;}
});
$('export').addEventListener('click',()=>{
  const columns=['日期','模型','未缓存输入','缓存输入','输出','总Token','未缓存费用','缓存费用','输出费用','已定价费用合计','待定价Tokens','币种','USD兑CNY汇率'];
  const multiplier=preferences.Currency==='CNY'?preferences.ExchangeRate:1;
  const rows=selectedRows.flatMap(day=>day.Models.map(row=>[day.DateLabel,row.Model,row.UncachedInput,row.CachedInput,row.Output,row.TotalTokens,row.UncachedInputCost*multiplier,row.CachedInputCost*multiplier,row.OutputCost*multiplier,row.EstimatedCost*multiplier,row.UnpricedTokens,preferences.Currency,preferences.ExchangeRate]));
  const quote=v=>'"'+String(v??'').replace(/^[=+@-]/,"'$&").replace(/"/g,'""')+'"';
  const blob=new Blob(['\ufeff'+[columns,...rows].map(row=>row.map(quote).join(',')).join('\r\n')],{type:'text/csv;charset=utf-8'});
  const url=URL.createObjectURL(blob),link=document.createElement('a');link.href=url;link.download=`codex-usage-${$('from').value}-${$('to').value}.csv`;link.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
});
window.addEventListener('hashchange',navigate);navigate();load();setInterval(load,5000);
window.addEventListener('focus',load);
setInterval(()=>{if(location.hash==='#settings')taskbarPreview();},1500);
let displayDay=today();
setInterval(()=>{freshness();reminderStatus();if(data){quotas();if(displayDay!==today()){displayDay=today();render();}}},1000);
