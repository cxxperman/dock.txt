// Проверка дока с выдуманной Kapsula: образцовый хаб на свободном порту, док без окна, проверки по dock.widgets.
'use strict';
const { spawn } = require('child_process');
const path = require('path');
const fs = require('fs');
const os = require('os');
const ROOT = process.argv[2];
const PORT = 48971;
const { connect } = require(path.join(ROOT, 'kit', 'hub', 'client.js'));

const stateDir = fs.mkdtempSync(path.join(os.tmpdir(), 'dock-hubtest-'));
const hub = spawn(process.execPath, [path.join(ROOT, 'kit', 'hub', 'hub.js'), String(PORT)], { stdio: 'ignore' });
let dock = null, failed = 0;
const sleep = ms => new Promise(r => setTimeout(r, ms));
const check = (ok, what) => { console.log((ok ? 'ok    ' : 'FAIL  ') + what); if (!ok) failed++; };

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
  await until(() => kapsula.online, 5000);
  check(kapsula.online, 'fake kapsula is connected to the reference hub on ' + PORT);

  dock = spawn(path.join(ROOT, 'dock', 'Dock.exe'), ['--headless', '--testhook', '--seconds', '40', '--state', stateDir], { env: Object.assign({}, process.env, { DOCK_HUB_PORT: String(PORT) }), stdio: 'ignore' });
  const shown = () => (states['dock.widgets'] && states['dock.widgets'].shown) || [];
  const badges = () => (states['dock.widgets'] && states['dock.widgets'].badges) || [];
  check(await until(() => states['dock.widgets'] && states['dock.settings'] && states['dock.apps'], 8000), 'dock publishes dock.settings, dock.apps and dock.widgets');
  console.log('      before kapsula data: shown = ' + JSON.stringify(shown()));
  check(shown().includes('clock') && !shown().includes('music') && !shown().includes('weather') && !shown().includes('calendar'), 'without kapsula states: clock yes; music, weather, calendar no');
  const groups = states['dock.settings'].groups;
  check(groups.length === 2 && groups[1].title === 'Виджеты' && groups[1].items.length === 7, 'dock.settings has the «Виджеты» group with 7 switches');

  let r = await kapsula.command('dock', 'test.act', { widget: 'music', action: 'next' });
  check(r.ok === false, 'music action is refused while there is no music widget (' + r.error + ')');

  kapsula.state('kapsula.media', { playing: true, title: 'Midnight City', artist: 'M83', app: 'Spotify' });
  kapsula.state('kapsula.weather', { temp: 8, text: 'Переменная облачность', icon: 'cloudy', city: 'Нью-Йорк' });
  kapsula.state('kapsula.calendar', { events: [{ title: 'Праздник', start: '00:00', end: '23:59', allDay: true }, { title: 'Поздно', start: '23:59', end: '23:59' }] });
  kapsula.state('kapsula.unread', { total: 15, apps: { discord: 3, steam: 12 } });
  check(await until(() => ['music', 'calendar', 'weather'].every(w => shown().includes(w)), 5000), 'after the four states: music, calendar and weather appear — shown = ' + JSON.stringify(shown()));
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
  check(r.ok === true && await until(() => !shown().includes('weather') && states['dock.settings'].groups[1].items[2].value === false, 3000), 'settings.set widgetWeather=false hides the weather widget and republishes settings');
  r = await kapsula.command('dock', 'settings.set', { key: 'badges', value: false });
  check(r.ok === true && await until(() => badges().length === 0, 3000), 'settings.set badges=false removes the badges');
  r = await kapsula.command('dock', 'settings.set', { key: 'widgetNope', value: true });
  check(r.ok === false, 'an unknown setting is refused (' + r.error + ')');
  const saved = JSON.parse(fs.readFileSync(path.join(stateDir, 'state.json'), 'utf8')).settings;
  check(saved.widgetWeather === false && saved.badges === false && saved.widgetClock === true, 'the switches are saved to state.json in the temp folder');
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
  kapsula.close ? kapsula.close() : kapsula.stop && kapsula.stop();
  const watcher = connect({ app: 'watcher', port: PORT, topics: ['dock.*'], onState(topic, data) { states[topic] = data; } });
  check(await until(() => !shown().includes('music'), 5000), 'kapsula disconnects: the hub nulls its states and the music widget goes away');

  r = await watcher.command('dock', 'test.quit', {});
  const exited = await until(() => dock.exitCode !== null, 5000);
  check(exited, 'dock exits on test.quit (exit code ' + dock.exitCode + ')');
  watcher.close ? watcher.close() : watcher.stop && watcher.stop();
})().catch(e => { console.log('FAIL  ' + (e && e.stack || e)); failed++; }).finally(() => {
  try { if (dock && dock.exitCode === null) dock.kill(); } catch (e) { }
  try { hub.kill(); } catch (e) { }
  try { fs.rmSync(stateDir, { recursive: true, force: true }); } catch (e) { }
  console.log(failed ? failed + ' failed' : 'hub test passed');
  setTimeout(() => process.exit(failed ? 1 : 0), 300);
});
