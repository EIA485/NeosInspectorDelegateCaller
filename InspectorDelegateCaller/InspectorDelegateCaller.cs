using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using NeosModLoader;
using FrooxEngine;
using FrooxEngine.UIX;
using BaseX;

namespace InspectorDelegateCaller
{
    public class InspectorDelegateCaller : NeosMod
    {
        public override string Name => "InspectorDelegateCaller";
        public override string Author => "eia485";
        public override string Version => "1.0.0";
        public override string Link => "https://github.com/EIA485/NeosInspectorDelegateCaller/";
        public override void OnEngineInit()
        {
            Harmony harmony = new Harmony("net.eia485.InspectorDelegateCaller");
            harmony.PatchAll();
        }

        [HarmonyPatch(typeof(WorkerInspector), "BuildInspectorUI")]
        class InspectorDelegateCallerPatch
        {
            static void Postfix(Worker worker, UIBuilder ui)
            {
                foreach (var m in worker.GetType().GetMethods(BindingFlags.FlattenHierarchy | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (!(m.ReturnType == typeof(void) && m.GetParameters().Length <= 1 && m.CustomAttributes.Any((a) => a.AttributeType == typeof(SyncMethod) || a.AttributeType.BaseType == typeof(SyncMethod)))) continue;
                    if (m.GetParameters().Length == 0)
                    {
                        LocaleString str = m.Name;
                        var b = ui.Button(in str);
                        b.Slot.AttachComponent<ButtonActionTrigger>().OnPressed.Target = (Action)m.CreateDelegate(typeof(Action), worker);
                    }
                    else
                    {
                        var p = m.GetParameters().First();
                        var pt = p.ParameterType;
                        if (pt.GetInterfaces().Contains(typeof(IWorldElement)))
                            callbackwitharg(true, worker, ui, m, p, pt);
                        else if (Coder.IsNeosPrimitive(pt))
                            callbackwitharg(false, worker, ui, m, p, pt);
                    }
                }
            }
        }
        static void callbackwitharg(bool isRef, Worker worker, UIBuilder ui, MethodInfo m, ParameterInfo p, Type pt)
        {
            ui.HorizontalLayout();
            LocaleString str = m.Name;
            var b = ui.Button(in str);
            var apt = typeof(Action<>).MakeGenericType(pt);
            Type t = (isRef ? typeof(CallbackRefArgument<>) : typeof(CallbackValueArgument<>)).MakeGenericType(pt);
            var c = b.Slot.AttachComponent(t);
            Type rt = typeof(SyncDelegate<>).MakeGenericType(apt);
            rt.GetProperty("Target").SetValue(t.GetField("Callback").GetValue(c), m.CreateDelegate(apt, worker));
            var cbrvn = isRef ? "Reference" : "Value";
            SyncMemberEditorBuilder.Build(c.GetSyncMember(cbrvn), p.Name, t.GetField(cbrvn), ui);
            b.Slot.AttachComponent<ButtonActionTrigger>().OnPressed.Target = (Action)t.GetMethod("Call").CreateDelegate(typeof(Action), c);
            ui.NestOut();
        }
    }
}