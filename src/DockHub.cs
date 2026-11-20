// dock.txt. DockHub.cs — связь с core.txt, лаунчером dev.txt. Он держит на этом компьютере хаб (127.0.0.1:42080).
// Док отдаёт туда свои настройки (они появляются на общей странице в core.txt), список приложений и виджетов,
// принимает оттуда изменения настроек и общий вид: «Меньше движения» для всех приложений разом.
// От kapsula.txt через хаб приходят музыка, погода, календарь и счётчики непрочитанного; ей же уходят кнопки музыки.
// Семья (Family.cs): от core.txt — core.family («Не беспокоить», игра на весь экран), ему — пункты дока для общего
// трея (dock.tray, команда tray.run) и горячие клавиши (dock.keys).
// core.txt может быть не запущен — это нормально: док работает сам по себе, а HubLink тихо пробует снова.
// Протокол описан в kit.txt (hub/PROTOCOL.md); HubLink.cs и Family.cs скопированы оттуда как есть.
using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

namespace DevTxt.DockApp
{
    class DockHub
    {
        // Ключ --testhook: док принимает ещё три команды — test.act (нажать кнопку виджета), test.state (что док решил
        // про игру и «Не беспокоить») и test.quit (выйти). Только для проверок.
        public static bool TestHook = false;

        readonly DockWindow window;
        readonly Store store;
        readonly HubLink link;
        readonly Family family;
        string lastSettings = "", lastApps = "", lastWidgets = "";

        // Для проверок: хаб на другом порту, рядом с настоящим core.txt (как KAPSULA_HUB_PORT у Kapsula)
        static int TestPort
        {
            get { int p; return int.TryParse(Environment.GetEnvironmentVariable("DOCK_HUB_PORT"), out p) && p > 0 ? p : 0; }
        }

        public DockHub(DockWindow window, Store store)
        {
            this.window = window;
            this.store = store;
            link = new HubLink("dock", "Dock", Program.VersionText, TestPort > 0 ? TestPort : HubLink.DefaultPort);
            link.Topics = new string[] { "hub.theme", "kapsula.media", "kapsula.weather", "kapsula.calendar", "kapsula.unread" };
            link.Commands = TestHook ? new string[] { "settings.set", "test.act", "test.state", "test.quit" } : new string[] { "settings.set" };
            link.OnCommand = Command;
            link.OnState = delegate(string topic, object data, string from)
            {
                if (topic == "hub.theme")
                {
                    var theme = data as Dictionary<string, object>;
                    object reduced = null;
                    bool on = theme != null && theme.TryGetValue("reducedMotion", out reduced) && reduced is bool && (bool)reduced;
                    window.Dispatcher.BeginInvoke(new Action(delegate { Ui.ForceReducedMotion = on; }));
                }
                else if (topic != null && topic.StartsWith("kapsula."))
                    window.Dispatcher.BeginInvoke(new Action(delegate { window.HubState(topic, data); }));
            };
            // Хаб пропал — он уже не скажет, что состояний Kapsula больше нет
            link.OnStatus = delegate(bool on) { if (!on) window.Dispatcher.BeginInvoke(new Action(delegate { window.HubGone(); })); };
            // Семья — после своих обработчиков и до link.Start(): она добавляет к ним core.family, hub.apps и tray.run.
            // Changed приходит в потоке хаба, а решает окно — в своём.
            family = new Family(link, "dock");
            family.Changed = delegate { window.Dispatcher.BeginInvoke(new Action(delegate { window.FamilyChanged(); })); };
        }

        public bool Online { get { return link.Online; } }

        // core.txt на связи и сказал, что сейчас у всех (core.family)
        public bool CoreOnline { get { return family.CoreOnline; } }
        // Впереди игра или видео на весь экран — по слову core.txt
        public bool Game { get { return family.Game; } }
        // «Не беспокоить», включённое в core.txt
        public bool Focus { get { return family.Focus; } }

        public void Start()
        {
            PublishFamily();
            link.Start();
            PublishSettings(true);
        }

        public void Stop() { link.Stop(); }

        // Просим core.txt показать окно — сразу на странице дока: настройки дока живут там
        public void ShowCore() { link.Command("core", "show", HubLink.Msg("page", FamilyRules.CorePage)); }

        // Пункты дока в общем трее core.txt и его горячие клавиши — по нынешним настройкам. Зовётся из потока окна
        // при каждой смене настроек; одно и то же второй раз в хаб не уходит (это проверяет Family).
        // При компактной панели Windows дока не видно — «Автоскрытие» в трее недоступно.
        public void PublishFamily()
        {
            family.SetTray(FamilyRules.Tray(store.AutoHide, store.Bar != "modern", delegate { OnWindow(window.ToggleAutoHide); }, ShowCore));
            family.SetKeys(FamilyRules.Keys(store.ReplaceTaskbar, store.WinKey));
        }

        // Пункт трея приходит в потоке хаба: выполняем его в потоке окна и отвечаем core.txt, когда всё уже сделано
        void OnWindow(Action action)
        {
            Exception failed = null;
            window.Dispatcher.Invoke(new Action(delegate { try { action(); } catch (Exception e) { failed = e; } }));
            if (failed != null) throw failed;
        }

        // Кнопки музыки: prev, toggle, next → команды media.prev, media.toggle, media.next для kapsula.txt
        public void Media(string action) { link.Command("kapsula", "media." + action, null); }

        // Состояние уходит целиком и только когда изменилось (или если просили обязательно)
        public void PublishSettings(bool always)
        {
            var now = store.Describe();
            string text = new JavaScriptSerializer().Serialize(now);
            if (!always && text == lastSettings) return;
            lastSettings = text;
            link.State("dock.settings", now);
        }

        public static object[] Apps(List<DockItem> items)
        {
            var list = new List<object>();
            foreach (var i in items) list.Add(HubLink.Msg("name", i.Name, "path", i.Path, "running", i.Running));
            return list.ToArray();
        }

        public void PublishApps(List<DockItem> items)
        {
            object[] now = Apps(items);
            string text = new JavaScriptSerializer().Serialize(now);
            if (text == lastApps) return;
            lastApps = text;
            link.State("dock.apps", now);
        }

        // dock.widgets: какие виджеты сейчас в доке и на каких значках счётчики
        public static Dictionary<string, object> Widgets(List<string> shown, List<KeyValuePair<string, int>> badges)
        {
            var list = new List<object>();
            foreach (var b in badges) list.Add(HubLink.Msg("name", b.Key, "count", b.Value, "text", UnreadInfo.Text(b.Value)));
            return HubLink.Msg("shown", shown.ToArray(), "badges", list.ToArray());
        }

        public void PublishWidgets(List<string> shown, List<KeyValuePair<string, int>> badges)
        {
            var now = Widgets(shown, badges);
            string text = new JavaScriptSerializer().Serialize(now);
            if (text == lastWidgets) return;
            lastWidgets = text;
            link.State("dock.widgets", now);
        }

        // Приходит в фоновом потоке; настройку меняем в потоке окна
        object Command(string name, Dictionary<string, object> args, string from)
        {
            if (name == "test.quit")
            {
                window.Dispatcher.BeginInvoke(new Action(delegate { window.Quit(); }));
                return null;
            }
            Exception failed = null;
            object answer = null;
            window.Dispatcher.Invoke(new Action(delegate
            {
                try
                {
                    if (name == "test.state") { answer = window.TestState(); return; }
                    if (name == "test.act")
                    {
                        object widget, action;
                        args.TryGetValue("widget", out widget);
                        args.TryGetValue("action", out action);
                        if (!window.WidgetAction(widget as string, action as string)) throw new Exception("такого виджета или действия сейчас нет");
                        return;
                    }
                    try
                    {
                        object key, value;
                        args.TryGetValue("key", out key);
                        args.TryGetValue("value", out value);
                        store.Apply(key as string, value);
                    }
                    finally { window.SettingsChanged(); }   // core.txt ждёт полную картину, даже если ничего не изменилось
                }
                catch (Exception e) { failed = e; }
            }));
            if (failed != null) throw failed;
            return answer;
        }
    }
}
