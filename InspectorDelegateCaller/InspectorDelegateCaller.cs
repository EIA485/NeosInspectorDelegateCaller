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
		public override string Version => "1.1.0";
		public override string Link => "https://github.com/EIA485/NeosInspectorDelegateCaller/";

		[AutoRegisterConfigKey] static ModConfigurationKey<bool> Key_Action = new("actions", "show callable direct actions in inspectors", () => true);
		[AutoRegisterConfigKey] static ModConfigurationKey<bool> Key_SubAction = new("subActions", "show callable non direct actions in inspectors, this is mainly logix impulses", () => true);
		[AutoRegisterConfigKey] static ModConfigurationKey<bool> Key_ArgAction = new("argActions", "show any action with arguments in inspectors", () => true);
		[AutoRegisterConfigKey] static ModConfigurationKey<bool> Key_Buttons = new("buttons", "show callable buttons in inspectors", () => false);
		[AutoRegisterConfigKey] static ModConfigurationKey<bool> Key_ArgButtons = new("argButtons", "show any button with arguments in inspectors", () => true);

		[AutoRegisterConfigKey] static ModConfigurationKey<bool> Key_ShowSlotDestroy = new("showSlotDestroy", "show the slot destroy button in inspectors", () => true);
		static ModConfiguration config;

		public override void OnEngineInit()
		{
			config = GetConfiguration();
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
					var param = m.GetParameters();

					if (m.ReturnType == typeof(void))
					{
						switch (param.Length)
						{
							case 0: //could have some branching mess here. may be marginally faster
								if (m.CustomAttributes.Any((a) => (a.AttributeType == typeof(SyncMethod) && config.GetValue(Key_Action)) || (a.AttributeType.BaseType == typeof(SyncMethod) && config.GetValue(Key_SubAction))))
								{
									LocaleString str = m.Name;

									// check for the slot destroy button
									if (str == "Destroy" && worker.GetType().FullName == "FrooxEngine.Slot" && config.GetValue(Key_ShowSlotDestroy) == false) break;

									var b = ui.Button(in str);
									b.Slot.AttachComponent<ButtonActionTrigger>().OnPressed.Target = (Action)m.CreateDelegate(typeof(Action), worker);
								}
								break;
							case 1:
								if (config.GetValue(Key_ArgAction) && hasSyncMethod(m))
								{
									var p = param[0];
									var pt = p.ParameterType;
									if (pt.GetInterfaces().Contains(typeof(IWorldElement)))
										actionCallbackwitharg(true, worker, ui, m, p, pt);
									else if (Coder.IsNeosPrimitive(pt))
										actionCallbackwitharg(false, worker, ui, m, p, pt);
								}
								break;
							case 2:
								if (config.GetValue(Key_Buttons) && isButtonDelegate(param) && hasSyncMethod(m))
								{
									LocaleString str = m.Name;
									var b = ui.Button(in str).Pressed.Target = (ButtonEventHandler)m.CreateDelegate(typeof(ButtonEventHandler), worker);
								}
								break;
							case 3:
								if (config.GetValue(Key_ArgButtons) && isButtonDelegate(param) && hasSyncMethod(m))
								{
									var p = param[2];
									var pt = p.ParameterType;
									if (pt.GetInterfaces().Contains(typeof(IWorldElement)))
										buttonCallbackwitharg(typeof(ButtonRefRelay<>), worker, ui, m, p, pt);
									else if (Coder.IsNeosPrimitive(pt))
										buttonCallbackwitharg(typeof(ButtonRelay<>), worker, ui, m, p, pt);
									else if (typeof(Delegate).IsAssignableFrom(pt))
										buttonCallbackwitharg(typeof(ButtonDelegateRelay<>), worker, ui, m, p, pt);
								}
								break;
						}
					}
				}
			}
		}
		static bool hasSyncMethod(MethodInfo info) => info.CustomAttributes.Any((a) => a.AttributeType == typeof(SyncMethod) || a.AttributeType.BaseType == typeof(SyncMethod));
		static bool isButtonDelegate(ParameterInfo[] param) => (param[1].ParameterType == typeof(ButtonEventData) || param[1].ParameterType.BaseType == typeof(ButtonEventData) && param[0].ParameterType.GetInterfaces().Contains(typeof(IButton)));
		static void actionCallbackwitharg(bool isRef, Worker worker, UIBuilder ui, MethodInfo m, ParameterInfo p, Type pt)
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
		static void buttonCallbackwitharg(Type genType, Worker worker, UIBuilder ui, MethodInfo m, ParameterInfo p, Type pt)
		{
			ui.HorizontalLayout();
			LocaleString str = m.Name;
			var b = ui.Button(in str);
			var bpt = typeof(ButtonEventHandler<>).MakeGenericType(pt);
			Type t = genType.MakeGenericType(pt);
			var c = b.Slot.AttachComponent(t);
			Type rt = typeof(SyncDelegate<>).MakeGenericType(bpt);
			rt.GetProperty("Target").SetValue(t.GetField("ButtonPressed").GetValue(c), m.CreateDelegate(bpt, m.IsStatic ? null : worker));
			SyncMemberEditorBuilder.Build(c.GetSyncMember("Argument"), p.Name, t.GetField("Argument"), ui);
			ui.NestOut();
		}
	}
}