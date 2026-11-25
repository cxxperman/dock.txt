// Проверка дока с выдуманными kapsula.txt и core.txt: образцовый хаб на свободном порту, док без окна (--headless),
// проверки по dock.settings, dock.widgets, dock.tray, dock.keys и проверочной команде test.state (ключ --testhook).
// Без окна док не прячет панель задач, не перехватывает кнопку Windows и не пишет в реестр — это здесь тоже проверяется.
//   node tools/hubtest.js <папка, где лежат kit и dock>
'use strict';
const { spawn, execFileSync } = require('child_process');
const path = require('path');
const fs = require('fs');
const os = require('os');
const ROOT = process.argv[2];
const PORT = 48971;   // не 42080: там настоящий core.txt
const { connect } = require(path.join(ROOT, 'kit', 'hub', 'client.js'));

const stateDir = fs.mkdtempSync(path.join(os.tmpdir(), 'dock-hubtest-'));
const hub = spawn(process.execPath, [path.join(ROOT, 'kit', 'hub', 'hub.js'), String(PORT)], { stdio: 'ignore' });
let dock = null, failed = 0;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const check = (ok, what) => { console.log((ok ? 'ok    ' : 'FAIL  ') + what); if (!ok) failed++; };
const ISLAND = ['widgetMusic', 'widgetCalendar', 'widgetWeather'];
// Компактная панель Windows живёт в реестре (TabletPostureTaskbar): только читаем — док без окна не должен её трогать
const compactValue = () => {
  try { return execFileSync('reg', ['query', 'HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer', '/v', 'TabletPostureTaskbar'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim(); }
  catch (e) { return 'absent'; }
};

(async () => {
  await sleep(600);
  const got = [];
  const states = {};
  const kapsula = connect({
    app: 'kapsula', name: 'Kapsula', version: 'test', port: PORT, topics: ['dock.*'],
    commands: {
      'media.prev': () => { got.push('media.prev'); return { ok: true }; },
      'media.toggle': () => { got.push('media.toggle'); return { ok: true }; },
      'media.next': () => { got.push('media.next'); return { ok: true }; },
    },
    onState(topic, data) { states[topic] = data; },
  });
  const until = async (fn, ms) => { const end = Date.now() + ms; while (Date.now() < end) { if (fn()) return true; await sleep(50); } return fn(); };
  // Что док решил про игру и «Не беспокоить» — через проверочную команду test.state. coreOnline и quiet в ней —
  // то, что известно прямо сейчас, а fullscreen и away док пересчитывает чуть позже, в потоке окна: ждём их, а не только связь
  const stateNow = async client => { const x = await client.command('dock', 'test.state', {}); return x.ok ? x.data : {}; };
  const waitState = async (client, fn, ms) => { const end = Date.now() + ms; let s = {}; while (Date.now() < end) { s = await stateNow(client); if (fn(s)) return s; await sleep(100); } return s; };
  const item = key => { for (const g of (states['dock.settings'] || { groups: [] }).groups) for (const i of g.items) if (i.key === key) return i; return {}; };
  const saved = () => JSON.parse(fs.readFileSync(path.join(stateDir, 'state.json'), 'utf8')).settings;
  await until(() => kapsula.online, 5000);
  check(kapsula.online, 'fake kapsula is connected to the reference hub on ' + PORT);

  dock = spawn(path.join(ROOT, 'dock', 'Dock.exe'), ['--headless', '--testhook', '--seconds', '90', '--state', stateDir], { env: Object.assign({}, process.env, { DOCK_HUB_PORT: String(PORT) }), stdio: 'ignore' });
  const shown = () => (states['dock.widgets'] && states['dock.widgets'].shown) || [];
  const badges = () => (states['dock.widgets'] && states['dock.widgets'].badges) || [];
  check(await until(() => states['dock.widgets'] && states['dock.settings'] && states['dock.apps'] && states['dock.tray'] && states['dock.keys'], 8000), 'dock publishes dock.settings, dock.apps, dock.widgets, dock.tray and dock.keys');
  console.log('      before kapsula data: shown = ' + JSON.stringify(shown()));
  check(shown().includes('clock') && !shown().includes('music') && !shown().includes('weather') && !shown().includes('calendar'), 'without kapsula states: clock yes; music, weather, calendar no');
  const groups = states['dock.settings'].groups;
  check(groups.length === 3 && groups[2].title === 'Виджеты' && groups[2].items.length === 7, 'dock.settings has three groups, the last is «Виджеты» with 7 switches');
  check(ISLAND.every(k => item(k).value === false) && ['widgetVolume', 'widgetBattery', 'widgetClock', 'badges'].every(k => item(k).value === true), 'a new dock: music, calendar and weather are off (the island shows them), volume, battery, clock and badges on');
  check(ISLAND.every(k => /остров kapsula\.txt/.test(item(k).hint)), 'their hints point to the island kapsula.txt');
  let r0;
  const sectioned = [];
  for (const g of groups) for (const i of g.items) if (i.section) sectioned.push(i.key + ':' + i.section);
  check(JSON.stringify(sectioned) === JSON.stringify(['autostart:startup']) && item('autostart').type === 'switch' && item('autostart').value === false && item('autostart').label === 'Запускать вместе с Windows',
    'only «Запускать вместе с Windows» moves to core.txt («Общие» → «Запуск и обновления»), off by default — ' + JSON.stringify(sectioned));
  r0 = await kapsula.command('dock', 'settings.set', { key: 'autostart', value: true });
  check(r0.ok === true && await until(() => item('autostart').value === true, 3000), 'settings.set autostart=true is accepted and republished (without a window the Windows registry is not touched)');
  await kapsula.command('dock', 'settings.set', { key: 'autostart', value: false });
  check(await until(() => item('autostart').value === false, 3000) && saved().autostart === undefined, 'autostart switches back off and is not kept in state.json: Windows itself remembers it');
  check(saved().islandWidgets === true, 'the first run writes the one-time mark to state.json');
  check(JSON.stringify(states['dock.tray']) === JSON.stringify({ items: [{ key: 'autoHide', label: 'Автоскрытие', check: false }, { key: 'settings', label: 'Настройки' }] }), 'dock.tray: «Автоскрытие» with a check mark and «Настройки», no «Открыть» — ' + JSON.stringify(states['dock.tray']));
  check(JSON.stringify(states['dock.keys']) === JSON.stringify({ keys: [{ id: 'winKey', label: 'Меню приложений', keys: 'Win', on: false }, { id: 'winDigits', label: 'Закреплённое приложение по номеру', keys: 'Win+1…9', on: false }] }), 'dock.keys: the Windows key and Win+1…9, off while dock.txt does not replace the taskbar — ' + JSON.stringify(states['dock.keys']));

  let r = await kapsula.command('dock', 'test.act', { widget: 'music', action: 'next' });
  check(r.ok === false, 'music action is refused while there is no music widget (' + r.error + ')');

  kapsula.state('kapsula.media', { playing: true, title: 'Midnight City', artist: 'M83', app: 'Spotify' });
  kapsula.state('kapsula.weather', { temp: 8, text: 'Переменная облачность', icon: 'cloudy', city: 'Нью-Йорк' });
  kapsula.state('kapsula.calendar', { events: [{ title: 'Праздник', start: '00:00', end: '23:59', allDay: true }, { title: 'Поздно', start: '23:59', end: '23:59' }] });
  kapsula.state('kapsula.unread', { total: 15, apps: { discord: 3, steam: 12 } });
  await sleep(1500);
  check(!['music', 'calendar', 'weather'].some(w => shown().includes(w)), 'with kapsula data but the switches off the dock does not repeat the island — shown = ' + JSON.stringify(shown()));
  for (const k of ISLAND) {
    r = await kapsula.command('dock', 'settings.set', { key: k, value: true });
    check(r.ok === true, 'settings.set ' + k + '=true accepted');
  }
  check(await until(() => ['music', 'calendar', 'weather'].every(w => shown().includes(w)), 5000), 'switched on, music, calendar and weather appear — shown = ' + JSON.stringify(shown()));
  check(ISLAND.every(k => saved()[k] === true) && saved().islandWidgets === true, 'the person\'s choice is saved and the mark stays: they will not be switched off again');
  console.log('      badges drawn (depends on what is pinned/running here): ' + badges().map(b => b.text).join(', '));
  check(badges().every(b => b.count > 0 && (b.count > 9 ? b.text === '9+' : b.text === String(b.count))), 'every published badge has a count and the right text');

  for (const a of ['prev', 'toggle', 'next']) {
    r = await kapsula.command('dock', 'test.act', { widget: 'music', action: a });
    check(r.ok === true, 'test.act music ' + a + ' accepted');
  }
  check(await until(() => got.length === 3, 3000) && got.join(',') === 'media.prev,media.toggle,media.next', 'kapsula received exactly media.prev, media.toggle, media.next — got ' + got.join(','));
  r = await kapsula.command('dock', 'test.act', { widget: 'volume', action: 'mute' });
  check(r.ok === false, 'the test hook cannot touch the volume');

  r = await kapsula.command('dock', 'settings.set', { key: 'widgetWeather', value: false });
  check(r.ok === true && await until(() => !shown().includes('weather') && item('widgetWeather').value === false, 3000), 'settings.set widgetWeather=false hides the weather widget and republishes settings');
  r = await kapsula.command('dock', 'settings.set', { key: 'badges', value: false });
  check(r.ok === true && await until(() => badges().length === 0, 3000), 'settings.set badges=false removes the badges');
  r = await kapsula.command('dock', 'settings.set', { key: 'widgetNope', value: true });
  check(r.ok === false, 'an unknown setting is refused (' + r.error + ')');
  check(saved().widgetWeather === false && saved().badges === false && saved().widgetClock === true, 'the switches are saved to state.json in the temp folder');
  await kapsula.command('dock', 'settings.set', { key: 'widgetWeather', value: true });
  await kapsula.command('dock', 'settings.set', { key: 'badges', value: true });
  check(await until(() => shown().includes('weather'), 3000), 'weather is back when switched on again');

  kapsula.state('kapsula.unread', { total: 0, apps: { discord: 0, steam: 0 } });
  check(await until(() => badges().length === 0, 3000), 'zeros in kapsula.unread: no badges');
  kapsula.state('kapsula.calendar', { events: [] });
  check(await until(() => !shown().includes('calendar'), 3000), 'an empty calendar hides the calendar widget');

  for (const t of ['media', 'weather', 'calendar', 'unread']) kapsula.state('kapsula.' + t, null);
  check(await until(() => !['music', 'calendar', 'weather'].some(w => shown().includes(w)) && shown().includes('clock'), 5000), 'after null states the widgets go away — shown = ' + JSON.stringify(shown()));
  r = await kapsula.command('dock', 'test.act', { widget: 'music', action: 'toggle' });
  check(r.ok === false && got.length === 3, 'no more media commands once the music widget is gone');

  kapsula.state('kapsula.media', { playing: false, title: 'Song', artist: '', app: '' });
  check(await until(() => shown().includes('music'), 3000), 'music comes back with a new state');
  kapsula.close();
  const watcher = connect({ app: 'watcher', port: PORT, topics: ['dock.*'], onState(topic, data) { states[topic] = data; } });
  check(await until(() => !shown().includes('music'), 5000), 'kapsula disconnects: the hub nulls its states and the music widget goes away');
  await until(() => watcher.online, 5000);

  // ── Семья: выдуманный core.txt — общий трей, клавиши, «Не беспокоить», игра на весь экран ──
  const shows = [];
  const core = connect({ app: 'core', name: 'core.txt', version: 'test', port: PORT, commands: { show: args => { shows.push(args); return null; }, 'focus.set': () => null } });
  check(await until(() => core.online, 5000), 'fake core.txt is connected');
  let s = await stateNow(core);
  check(s.coreOnline === false, 'core.txt is connected but has not said core.family yet: the dock still decides by itself — ' + JSON.stringify(s));
  check(s.taskbarTaken === false && s.winKeyHooked === false, 'headless: the taskbar is not touched and the Windows key is not hooked');

  r = await core.command('dock', 'tray.run', { key: 'autoHide' });
  check(r.ok === true && await until(() => states['dock.tray'].items[0].check === true && item('autoHide').value === true, 3000), 'tray.run autoHide switches auto-hide on; dock.tray comes again with the check mark, dock.settings with the new value');
  check(saved().autoHide === true && (await stateNow(core)).autoHide === true, 'auto-hide from the tray is saved like any other setting');
  r = await core.command('dock', 'tray.run', { key: 'autoHide' });
  check(r.ok === true && await until(() => states['dock.tray'].items[0].check === false && item('autoHide').value === false, 3000), '…and off again');
  r = await core.command('dock', 'tray.run', { key: 'settings' });
  check(r.ok === true && await until(() => shows.length === 1, 3000) && shows[0] && shows[0].page === 'app:dock', 'tray.run settings asks core.txt to show the dock page — got ' + JSON.stringify(shows));
  r = await core.command('dock', 'tray.run', { key: 'show' });
  check(r.ok === false, 'a tray item the dock does not have is refused (' + r.error + ')');

  s = await stateNow(core);
  check(s.appBar && s.appBar.wanted === false && s.appBar.registered === false, 'next to the Windows taskbar the dock would take no place at the bottom — ' + JSON.stringify(s.appBar));
  r = await core.command('dock', 'settings.set', { key: 'bar', value: 'dock' });
  check(r.ok === true && await until(() => states['dock.keys'].keys[0].on === true && states['dock.keys'].keys[1].on === true, 3000), 'dock.txt instead of the taskbar: dock.keys lists the Windows key and Win+1…9 as on');
  s = await stateNow(core);
  check(s.taskbarTaken === false && s.winKeyHooked === false && s.winDigits === false, 'even then a headless dock hides no taskbar and hooks no key — ' + JSON.stringify(s));
  const ab = s.appBar || {};
  check(ab.wanted === true && ab.registered === false && ab.height === Math.ceil(ab.restDip * ab.scale - 1e-6) && ab.rect[3] - ab.rect[1] === ab.height && ab.rect[2] > ab.rect[0],
    'a visible dock instead of the taskbar would take a band as high as the dock at rest at the bottom of the main monitor; without a window nothing is registered — ' + JSON.stringify(ab));
  check(s.edgeDwellMs >= 300 && s.edgeDwellMs <= 400 && s.shellHook === false && s.fallbackMs >= 10000 && s.fallbackMs <= 15000, 'auto-hide waits ' + s.edgeDwellMs + ' ms at the edge; without a window no shell hook, the backup poll is ' + s.fallbackMs + ' ms');
  r = await core.command('dock', 'settings.set', { key: 'autoHide', value: true });
  s = await stateNow(core);
  check(r.ok === true && s.appBar.wanted === false && s.appBar.registered === false, 'with auto-hide the dock would take no place — ' + JSON.stringify(s.appBar));
  await core.command('dock', 'settings.set', { key: 'autoHide', value: false });
  r = await core.command('dock', 'settings.set', { key: 'winKey', value: false });
  check(r.ok === true && await until(() => states['dock.keys'].keys[0].on === false && states['dock.keys'].keys[1].on === true, 3000), 'the Windows key setting off: listed as off, Win+1…9 stay on');
  await core.command('dock', 'settings.set', { key: 'winKey', value: true });

  // ── Окно просит внимания: будто Windows прислала HSHELL_FLASH (test.shell). Без окна дока прыжков нет — сразу точка ──
  r = await core.command('dock', 'test.shell', { event: 'flash' });
  if (r.ok) {
    const who = r.data.name;
    s = await waitState(core, x => (x.attention || []).some(a => a.name === who), 3000);
    check((s.attention || []).some(a => a.name === who && a.phase === 'mark'), 'a window asks for attention: its app is marked (no hops without a window) — ' + JSON.stringify(s.attention));
    r = await core.command('dock', 'test.shell', { event: 'activated', app: who });
    s = await waitState(core, x => !(x.attention || []).some(a => a.name === who), 3000);
    check(r.ok === true && !(s.attention || []).some(a => a.name === who), 'the window came forward: the mark is gone — ' + JSON.stringify(s.attention));
  } else console.log('      (no running app whose window is not ahead — attention not checked: ' + r.error + ')');
  r = await core.command('dock', 'test.shell', { event: 'nope' });
  check(r.ok === false, 'an unknown shell message is refused (' + r.error + ')');
  // Компактная панель Windows: дока не видно — «Автоскрытие» в трее видно, но не нажимается
  const compactBefore = compactValue();
  r = await core.command('dock', 'settings.set', { key: 'bar', value: 'modern' });
  check(r.ok === true && await until(() => states['dock.tray'].items[0].enabled === false && states['dock.keys'].keys[0].on === false, 3000), 'the compact Windows bar: «Автоскрытие» in the tray is greyed out, the Windows key is off — ' + JSON.stringify(states['dock.tray']));
  r = await core.command('dock', 'tray.run', { key: 'autoHide' });
  check(r.ok === false && saved().autoHide === false, 'the greyed-out tray item is refused and changes nothing (' + r.error + ')');
  s = await stateNow(core);
  check(s.taskbarTaken === false && compactValue() === compactBefore, 'a headless dock writes nothing to the registry for the compact bar and restarts nothing — ' + compactBefore);
  await core.command('dock', 'settings.set', { key: 'bar', value: 'both' });
  check(await until(() => states['dock.keys'].keys[0].on === false && item('bar').value === 'both' && item('winKey').value === true && states['dock.tray'].items[0].enabled === undefined, 3000), 'back to both bars: the key is off again, «Автоскрытие» works again');

  core.state('core.family', { tray: true, focus: true, focusUntil: 0, game: false, gameApp: '' });
  s = await waitState(core, x => x.coreOnline === true && x.quiet === true && x.fullscreen === false, 3000);
  check(s.coreOnline === true && s.quiet === true && s.fullscreen === false && s.away === false, '«Не беспокоить» in core.family: the dock is quiet (badges stay, without the pop), no game — ' + JSON.stringify(s));
  // Игра: док уходит, только если окно впереди на главном мониторе — там, где он стоит (screen в test.state).
  // Решение док применяет в потоке окна: сразу, как пришло core.family, и потом раз в секунду. Поэтому ждём дольше
  // секунды и смотрим один раз: если окно впереди на другом мониторе, правильный ответ — ничего не менять
  core.state('core.family', { tray: true, focus: false, focusUntil: 0, game: true, gameApp: 'game' });
  await waitState(core, x => x.coreGame === true, 3000);
  await sleep(1500);
  s = await stateNow(core);
  check(s.coreGame === true && s.fullscreen === s.screen && s.away === s.screen && s.quiet === false,
    (s.screen ? 'core.family.game, the window ahead is on the main monitor: the dock steps aside' : 'core.family.game, but the window ahead is on another monitor: the dock stays on the main one') + ' — ' + JSON.stringify(s));
  core.state('core.family', { tray: true, focus: false, focusUntil: 0, game: false, gameApp: '' });
  s = await waitState(core, x => x.coreGame === false && x.fullscreen === false, 3000);
  check(s.fullscreen === false && s.away === false, 'the game left: the dock is on screen — ' + JSON.stringify(s));
  core.state('core.family', { tray: true, focus: false, focusUntil: 0, game: true, gameApp: 'game' });
  s = await waitState(core, x => x.coreGame === true && x.away === x.screen, 3000);
  core.close();
  s = await waitState(watcher, x => x.coreOnline === false && x.fullscreen === (x.local && x.screen), 5000);
  check(s.coreOnline === false && s.fullscreen === (s.local && s.screen) && s.away === s.fullscreen, 'core.txt left: the dock checks by itself again (its own check says ' + s.local + ', main monitor ' + s.screen + ') — ' + JSON.stringify(s));

  r = await watcher.command('dock', 'test.quit', {});
  const exited = await until(() => dock.exitCode !== null, 5000);
  check(exited, 'dock exits on test.quit (exit code ' + dock.exitCode + ')');
  watcher.close();
})().catch(e => { console.log('FAIL  ' + (e && e.stack || e)); failed++; }).finally(() => {
  try { if (dock && dock.exitCode === null) dock.kill(); } catch (e) { }
  try { hub.kill(); } catch (e) { }
  try { fs.rmSync(stateDir, { recursive: true, force: true }); } catch (e) { }
  console.log(failed ? failed + ' failed' : 'hub test passed');
  setTimeout(() => process.exit(failed ? 1 : 0), 300);
});
