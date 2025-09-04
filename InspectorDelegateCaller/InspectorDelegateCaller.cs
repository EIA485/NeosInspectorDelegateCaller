using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.NET.Common;
using BepInExResoniteShim;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.ProtoFlux;
using FrooxEngine.ProtoFlux.CoreNodes;
using FrooxEngine.ProtoFlux.Runtimes.Execution;
using FrooxEngine.ProtoFlux.Runtimes.Execution.Nodes;
using FrooxEngine.UIX;
using HarmonyLib;
using ProtoFlux.Core;
using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;
using static FrooxEngine.InteractionHandler;

namespace InspectorDelegateCaller
{
	[ResonitePlugin(PluginMetadata.GUID, PluginMetadata.NAME, PluginMetadata.VERSION, PluginMetadata.AUTHORS, PluginMetadata.REPOSITORY_URL)]
	[BepInDependency(BepInExResoniteShim.PluginMetadata.GUID)]
	public class InspectorDelegateCaller : BasePlugin
	{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider adding the 'required' modifier or declaring as nullable.
		static ManualLogSource log;
		static ConfigEntry<bool> Actions, SubAction, ArgAction, Buttons, ArgButtons, FallBack, RegexDebuging, DefaultArgState, DisableHeaderText;
		static ConfigEntry<String> BlockerRegex, DisableCustomRegex;
#pragma warning restore CS8618 // its fine since the code that makes these not null is run before anything else.
		static Regex? blocRegex, custRegex;
		const string GrabNodeTag = "nodeGrab";
		public override void Load()
		{
			Actions = Config.Bind(PluginMetadata.NAME, "actions", true, "show callable direct actions in inspectors");
			SubAction = Config.Bind(PluginMetadata.NAME, "subActions", true, "show callable non direct actions in inspectors");
			ArgAction = Config.Bind(PluginMetadata.NAME, "argActions", true, "show any action with arguments in inspectors");
			Buttons = Config.Bind(PluginMetadata.NAME, "buttons", true, "show callable buttons in inspectors");
			ArgButtons = Config.Bind(PluginMetadata.NAME, "argButtons", true, "show any button with arguments in inspectors");
			FallBack = Config.Bind(PluginMetadata.NAME, "fallBack", true, "show any other delegate with arguments in inspectors");
			DefaultArgState = Config.Bind(PluginMetadata.NAME, "defaultArgState", true, "default arg inputs visable");
			BlockerRegex = Config.Bind(PluginMetadata.NAME, "blockerRegex", "^((Slot|StaticGaussianSplat|VideoTextureProvider|MeshRenderer|FingerReferencePoseSource)\\.|((StaticTexture\\dD\\.|StaticTextureProvider.*?\\.)(Invert|Swap|Rotate|Flip|Tile|Adjust|Alpha|Grayscale|ShiftHue|Resize|MakeSquare|ToNearestPOT|InvalidFloats|GenerateBitmapMetadata|ReplaceFromClipboard|Trim[a-zA-Z]|(?!Color).*?Alpha(\\[| )|Normalize[a-zA-Z]|.*?(White|Black).*?\\[))|.+?\\.(((On)?Bake)(.*?(\\[IButton, ButtonEventData\\])|[a-zA-Z]*?$)|OnSetupRenderer)|ProceduralTexture3DBase\\.OnSpawnVisualizer|StaticAudioClip\\.[^A]|ProtoFluxNode\\.OnDumpStructure|SkinnedMeshRenderer\\.(SortBlendshapesBy|Visualize|StripEmpty|ExtendExplicitBoundsFromPose|ComputeExplicitBoundsFromPose|MergeBlendshapes|SeparateOutBlendshapes|ClearBoundsVisuals|ResetBonesToBindPoses))", "regex string that defines what delegates to skip"); //reasonable default filter
			RegexDebuging = Config.Bind(PluginMetadata.NAME, "regexDebug", false, "log each time regex is used and what if anything matched");
			DisableCustomRegex = Config.Bind(PluginMetadata.NAME, "customInspectorDisableRegex", "PBS_Slice|AudioZitaReverb|PagingControl|DataPreset|RootSpace|MazeGenerator|HapticManager|PhysicalLocomotion|.*?Collider|DynamicBoneChain|GridContainer|DataFeedItemMapper|RootCategoryView|BreadcrumbManager|Workspace|AmbientLightSH2|Animator|DirectVisemeDriver|MaterialSet|GaussianSplatRenderer|Rig|Skybox|BipedRig|HandPoser|BooleanSwitcher|LookAt|DynamicBlendShapeDriver|ObjectRoot|ScaleObjectManager|CommonAvatarBuilder|SimpleAwayIndicatoCubemapCreator|SettingComponent|ItemTextureThumbnailSource|AvatarExpressionDriver|EyeRotationDriver\\+Eye|SimpleAvatarProtection|PhotonDust\\+.*?Lifetime|VRIKAvatar", "regex string that defines what workers should use the default inspector generation"); //default filter just blocks customInspectors only adding their own buttons
			DisableHeaderText = Config.Bind(PluginMetadata.NAME, "disableHeaderText", false, "Disables the warning text some components have after the header");
			log = Log;

			BlockerRegex.SettingChanged += BlockerRegex_SettingChanged;
			BlockerRegex_SettingChanged();

			DisableCustomRegex.SettingChanged += DisableCustomInspectorsRegex_SettingChanged;
			DisableCustomInspectorsRegex_SettingChanged();

			HarmonyInstance.PatchAll();
		}

		private void BlockerRegex_SettingChanged(object? sender = null, EventArgs e = null) => UpdateRegex(ref blocRegex, BlockerRegex);
		private void DisableCustomInspectorsRegex_SettingChanged(object? sender = null, EventArgs e = null) => UpdateRegex(ref custRegex, DisableCustomRegex);

		[HarmonyPatch(typeof(WorkerInspector))]
		class InspectorDelegateCallerPatch
		{
			static readonly Regex NonDigits = new Regex(@"\D", RegexOptions.Compiled);

			static ICustomInspector CallProxy(Worker worker)
			{
				if (worker is ICustomInspector custom &&
					!IsMatch(DisableCustomRegex, custRegex, custom.GetType().GetNiceName()))
					return custom;
				return null;
			}


			[HarmonyTranspiler]
			[HarmonyPatch("BuildUIForComponent")]
			static IEnumerable<CodeInstruction> BuildUIForComponentTranspiler(IEnumerable<CodeInstruction> codes) => ICustomInspectorTranspiler(codes, OpCodes.Ldarg_1);

			[HarmonyTranspiler]
			[HarmonyPatch(typeof(SyncMemberEditorBuilder), "BuildSyncObject")]
			static IEnumerable<CodeInstruction> BuildSyncObjectTranspiler(IEnumerable<CodeInstruction> codes) => ICustomInspectorTranspiler(codes, OpCodes.Ldarg_0);

			static IEnumerable<CodeInstruction> ICustomInspectorTranspiler(IEnumerable<CodeInstruction> codes, OpCode LoadCode)
			{
				bool search = true;
				CodeInstruction last = null;
				foreach (var inst in codes)
				{
					if (search)
					{
						if (inst.opcode == OpCodes.Isinst && inst.operand == typeof(ICustomInspector) &&
							last?.opcode == LoadCode)
						{
							search = false;
							yield return new(OpCodes.Call, AccessTools.Method(typeof(InspectorDelegateCallerPatch), nameof(CallProxy)));
							continue;
						}
						last = inst;
					}
					yield return inst;
				}
			}


			[HarmonyPrefix]
			[HarmonyPatch("AddHeaderText")]
			static bool AddHeaderTextPrefix() => !DisableHeaderText.Value;

			//seemed like a good idea but upon closer inspection this is already always null from what i see.
			//[HarmonyPrefix]
			//[HarmonyPatch(nameof(WorkerInspector.BuildInspectorUI))]
			//static void BuildInspectorUIPrefix(ref Predicate<ISyncMember> memberFilter)
			//{
			//	if (DisableCustomInspectors.Value) memberFilter = null;
			//}

			[HarmonyPrefix] //fixes edge cases that arise when CustomInspectors are skipped
			[HarmonyPatch(nameof(WorkerInspector.BuildInspectorUI))]
			static void BuildInspectorUIPrefix(UIBuilder ui)
			{
				ui.Style.MinHeight = 24f;
			}

			[HarmonyPostfix]
			[HarmonyPatch(nameof(WorkerInspector.BuildInspectorUI))]
			static void BuildInspectorUIPostfix(Worker worker, UIBuilder ui)
			{

				foreach (var m in GetAllMethods(worker.GetType()))
				{
					if (!hasSyncMethod(m)) continue;
					var param = m.GetParameters();
					if (param.Length > 8) return;

					string args = param.Length > 0 ? '[' + string.Join(", ", param.Select(p => p.ParameterType.Name)) + ']' : "";
					string mret = m.ReturnType != typeof(void) ? $" : {m.ReturnType.GetNiceName()}" : "";
					string fullName = $"{m.DeclaringType.GetNiceName()}.{m.Name}{args}{mret}";


					if (IsMatch(BlockerRegex, blocRegex, fullName)) continue;

					if (m.ReturnType == typeof(void))
					{
						switch (param.Length)
						{
							case 0:
								if (m.CustomAttributes.Any(a => a.AttributeType == typeof(SyncMethod)))
								{
									if (!Actions.Value) continue;
									actionui(worker, ui, m);
									continue;
								}
								else
								{
									if (!SubAction.Value) continue;
									actionui(worker, ui, m);
									continue;
								}
								break;
							case 1:
								{
									if (!ArgAction.Value) continue;
									var p = param[0];
									var pt = p.ParameterType;
									if (pt.GetInterfaces().Contains(typeof(IWorldElement)))
										actionCallbackwitharg(true, worker, ui, m, p, pt);
									else if (Coder.IsEnginePrimitive(pt))
										actionCallbackwitharg(false, worker, ui, m, p, pt);
									continue;
								}
								break;
							case 2:
								if (isButtonDelegate(param))
								{
									if (!Buttons.Value) continue;
									var Delegate = StaticSafeCreateDelegate<ButtonEventHandler>(m, worker);
									LocaleString str = m.Name;
									var b = ui.Button(in str);
									b.Pressed.Target = Delegate;
									proxySource(b, Delegate, typeof(ButtonEventHandler));
									continue;
								}
								break;
							case 3:
								if (isButtonDelegate(param))
								{
									if (!ArgButtons.Value) continue;
									var p = param[2];
									var pt = p.ParameterType;
									if (pt.GetInterfaces().Contains(typeof(IWorldElement)))
										buttonCallbackwitharg(typeof(ButtonRefRelay<>), worker, ui, m, p, pt);
									else if (Coder.IsEnginePrimitive(pt))
										buttonCallbackwitharg(typeof(ButtonRelay<>), worker, ui, m, p, pt);
									else if (typeof(Delegate).IsAssignableFrom(pt))
										buttonCallbackwitharg(typeof(ButtonDelegateRelay<>), worker, ui, m, p, pt);
									continue;
								}
								break;
						}
					}
					if (FallBack.Value)
					{
						bool isAsync;
						bool isRetRef;
						Type delegateType;
						Type returnType;
						var type = GetDelegateNodeType(m, out isAsync, out isRetRef, out delegateType, out returnType);
						if (type == null || delegateType == null) continue;


						var topLayout = ui.HorizontalLayout();
						topLayout.Slot.GetComponent<LayoutElement>().MinHeight.Value = -1f;

						Slot code = topLayout.Slot.AddSlot("Logix");


						ProtoFluxNode DelegateProxy = (ProtoFluxNode)code.AttachComponent(type);
						var Delegate = CreateDelegateGlobal(DelegateProxy, delegateType, m, worker);


						var callProxyType = isAsync ? ProtoFluxHelper.AsyncCallInput : ProtoFluxHelper.SyncCallInput;
						ProtoFluxNode callProxy = (ProtoFluxNode)code.AttachComponent(callProxyType);
						DelegateProxy.TryConnectImpulse(callProxy.GetImpulse(0), DelegateProxy.GetOperation(0), undoable: false);

						bool hasReturn = returnType != typeof(void);
						bool hasParams = DelegateProxy.NodeInputCount > 0;
						Button ProxyButton = null;
						if (hasParams)
						{
							LocaleString ExpName = "|";
							var ExpButton = ui.Button(ExpName, colorX.Gray);
							ExpButton.Slot.GetComponent<LayoutElement>().MinWidth.Value = 30f;
							ProxyButton = ExpButton;

							var argsCall = ui.VerticalLayout();
							var acLayout = argsCall.Slot.GetComponent<LayoutElement>();
							acLayout.MinHeight.Value = -1f;
							acLayout.FlexibleWidth.Value = 1f;

							var ArgsLayout = ui.VerticalLayout();
							var Exp = ExpButton.Slot.AttachComponent<Expander>();
							Exp.SectionRoot.Target = ArgsLayout.Slot;
							ArgsLayout.Slot.ActiveSelf = DefaultArgState.Value;
							ArgsLayout.Slot.GetComponent<LayoutElement>().MinHeight.Value = -1f;
						}
						foreach (var input in DelegateProxy.NodeInputs)
						{
							var inputNodeType = ProtoFluxHelper.GetInputNode(input.GetType().GetGenericArguments()[0].GetGenericArguments()[0]);
							var inputNode = (ProtoFluxNode)code.AttachComponent(inputNodeType);

							if ((inputNode.GetSyncMember("Value") ?? inputNode.GetSyncMember("Target")) is ISyncMember syncMember)
							{
								var fieldInfo = inputNodeType.GetField(input.Name);
								DelegateProxy.TryConnectInput(input, inputNode.GetOutput(0), allowExplicitCast: true, undoable: false);
								var paramInfo = param[int.Parse(NonDigits.Replace(input.Name, ""))];
								SyncMemberEditorBuilder.Build(syncMember, paramInfo.Name, fieldInfo, ui);
								if(paramInfo.DefaultValue != null && paramInfo.DefaultValue.GetType().IsAssignableTo(((IField)syncMember).ValueType)) ((IField)syncMember).BoxedValue = paramInfo.DefaultValue;
							}
						}
						if (hasParams) ui.NestOut();

						if (hasReturn) ui.HorizontalLayout().Slot.AttachComponent<LayoutElement>();
						var callTrigMeth = AccessTools.Method(callProxyType, "OnTrigger");
						LocaleString localMethName = m.Name;
						var b = ui.Button(in localMethName);
						b.Pressed.Target = StaticSafeCreateDelegate<ButtonEventHandler>(callTrigMeth, callProxy);


						ProxyButton ??= b;
						proxySource(ProxyButton, Delegate, delegateType);


						if (hasReturn)
						{
							var writeType = (isRetRef ? typeof(ObjectWrite<,>) : typeof(ValueWrite<,>)).MakeGenericType(typeof(FrooxEngineContext), returnType);
							var writeNode = (ProtoFluxNode)code.AttachComponent(writeType);
							var refSoruceType = isRetRef ? ProtoFluxHelper.ReferenceSource.MakeGenericType(returnType) : ProtoFluxHelper.GetSourceNode(returnType);
							var refSource = (ProtoFluxNode)code.AttachComponent(refSoruceType);

							writeNode.TryConnectImpulse(DelegateProxy.GetImpulse(0), writeNode.GetOperation(0), undoable: false);
							DelegateProxy.TryConnectInput(writeNode.GetInput(0), DelegateProxy.GetOutput(0), true, undoable: false);
							writeNode.TryConnectReference(writeNode.GetReference(0), refSource, undoable: false);

							var displayButton = ui.Button(localMethName);
							displayButton.BaseColor.Value = colorX.Red;
							var displayText = displayButton.Slot.GetComponentInChildren<Text>();

							if (isRetRef)
							{
								var refFieldType = typeof(ReferenceField<>).MakeGenericType(returnType);
								var refField = displayButton.Slot.AttachComponent(refFieldType);
								var refFieldField = refField.GetSyncMember("Reference");
								((ISource)refSource).TrySetRootSource(refFieldField);

								//i don't want the RefEditor's TryReceive to work.
								var refFieldReadOnly = displayButton.Slot.AttachComponent(refFieldType);
								var refReadOnlyField = (IField)refFieldReadOnly.GetSyncMember("Reference");

								var refCopy = displayButton.Slot.AttachComponent(typeof(ReferenceCopy<>).MakeGenericType(returnType));
								((ISyncRef)refCopy.GetSyncMember("Target")).Target = refReadOnlyField;
								((ISyncRef)refCopy.GetSyncMember("Source")).Target = refFieldField;


								var refEditor = displayButton.Slot.AttachComponent<RefEditor>();
								((ISyncRef)refEditor.GetSyncMember("_targetRef")).Target = refReadOnlyField;
								((ISyncRef)refEditor.GetSyncMember("_textDrive")).Target = displayText.Content;

								var RefSet = displayButton.Slot.AttachComponent(typeof(ButtonReferenceSet<>).MakeGenericType(returnType));
								((ISyncRef)RefSet.GetSyncMember("TargetReference")).Target = refFieldField;
							}
							else
							{

								var ProxySoruce = displayButton.Slot.AttachComponent(typeof(ValueProxySource<>).MakeGenericType(returnType));
								var valuefield = ProxySoruce.GetSyncMember("Value");
								((ISource)refSource).TrySetRootSource(valuefield);

								var textDriver = displayButton.Slot.AttachComponent<MultiValueTextFormatDriver>();
								textDriver.Sources.Add((IField)valuefield);
								textDriver.Format.Value = "{0}";
								textDriver.Text.Target = displayText.Content;


								var ValSet = displayButton.Slot.AttachComponent(typeof(ButtonValueSet<>).MakeGenericType(returnType));
								((ISyncRef)ValSet.GetSyncMember("TargetValue")).Target = valuefield;
								((IField)ValSet.GetSyncMember("SetValue")).BoxedValue = returnType.GetDefault();
							}
						}
						if (hasParams) ui.NestOut();
						if (hasReturn) ui.NestOut();
						ui.NestOut();
					}
				}
			}
			static FieldInfo currentInteractableInfo = AccessTools.Field(typeof(Canvas.InteractionData), "_currentInteractable");
			static FieldInfo _laserGrabDistance = AccessTools.Field(typeof(InteractionHandler), "_laserGrabDistance");
			static FieldInfo _grabHitSlot = AccessTools.Field(typeof(InteractionHandler), "_grabHitSlot");
			static FieldInfo _grabInteractionTarget = AccessTools.Field(typeof(InteractionHandler), "_grabInteractionTarget");
			static FieldInfo _isScaling = AccessTools.Field(typeof(InteractionHandler), "_isScaling");
			[HarmonyPrefix]
			[HarmonyPatch(typeof(Canvas), nameof(Canvas.TryGrab))]
			static bool TryGrabPrefix(ref IGrabbable __result, Component grabber, Canvas __instance, Dictionary<Component, Canvas.InteractionData> ____currentInteractions, in float3 point)
			{
				____currentInteractions.TryGetValue(grabber, out var value);
				if (value == null) return true;
				var interactable = (IUIInteractable)currentInteractableInfo.GetValue(value);
				if (interactable == null) return true;
				var soruce = interactable.Slot.GetComponentInParents<IDelegateProxySource>((d) => ((Component)d).Slot.Tag == GrabNodeTag);
				if (soruce == null) return true;

				//i want to still create the node if the target delegate is null.
				var DelegateType = soruce.DelegateType;
				var m = AccessTools.Method(DelegateType, "Invoke") ?? soruce.Delegate.Method;
				var NodeType = GetDelegateNodeType(m, out DelegateType);
				if (NodeType == null) return true;

				Slot nodeSlot = __instance.LocalUserSpace.AddSlot(NodeType.Name);
				var Point = point;
				nodeSlot.GlobalPosition = point;
				nodeSlot.GlobalRotation = __instance.Slot.GlobalRotation;
				nodeSlot.GlobalScale = __instance.LocalUserRoot.GlobalScale * float3.One;
				var node = (ProtoFluxNode)nodeSlot.AttachComponent(NodeType);
				node.EnsureVisual();
				if (soruce.Delegate != null)
				{
					CreateDelegateGlobal(node, DelegateType, soruce.Delegate.Method, soruce.Delegate.Target);
				}
				var grabbable = nodeSlot.GetComponent<Grabbable>();

				__result = grabbable; //block grabbing others

				//force laser grab. recreating the InteractionHandler.Grab laser codepath
				//if there is a better way of doing this please send me a pr.
				if (grabber.Slot.FindInteractionHandler() is InteractionHandler commonTool)
				{
					__instance.RunInUpdates(0, () =>
					{
						((Sync<GrabType>)commonTool.GetSyncMember("_currentGrabType")).Value = GrabType.Laser;
						commonTool.Grabber.Grab(grabbable);
						var holderSlot = commonTool.Grabber.HolderSlot;
						if (!holderSlot.Position_Field.IsDriven) holderSlot.GlobalPosition = commonTool.Laser.CurrentActualPoint;
						((Sync<floatQ>)commonTool.GetSyncMember("_holderRotationOffset")).Value = floatQ.Identity;
						IViewTargettingController activeTargetting = __instance.World.GetScreen()?.ActiveTargetting.Target;
						if (__instance.InputInterface.ScreenActive && (activeTargetting is UI_TargettingController || activeTargetting is FreeformTargettingController))
							((Sync<floatQ?>)commonTool.GetSyncMember("_holderRotationReference")).Value = __instance.World.LocalUserViewRotation;
						else
							((Sync<floatQ?>)commonTool.GetSyncMember("_holderRotationReference")).Value = null;

						((Sync<LaserRotationType>)commonTool.GetSyncMember("_laserRotationType")).Value = LaserRotationType.AxisY;
						((Sync<float>)commonTool.GetSyncMember("_originalTwistOffset")).Value = commonTool.TwistAngle;

						float? pointDistance = null;
						if (__instance.World.GetScreen() is ScreenController screen)
						{
							float3 globalDirection = commonTool.Laser.GlobalCurrentPoint;
							pointDistance = screen.GetPointViewDistance(in globalDirection);
						}
						if (pointDistance.HasValue)
						{
							_laserGrabDistance.SetValue(commonTool, commonTool.Laser.Slot.GlobalScaleToLocal(pointDistance.Value));
						}
						else
						{
							_laserGrabDistance.SetValue(commonTool, commonTool.Laser.CurrentPointDistance);
						}
						_grabHitSlot.SetValue(commonTool, commonTool.Laser.CurrentHit);
						_grabInteractionTarget.SetValue(commonTool, commonTool.Laser.CurrentInteractionTarget);
						_isScaling.SetValue(commonTool, true); commonTool.Grabber.Grab(grabbable);

						nodeSlot.LocalPosition = float3.Zero;
						nodeSlot.LocalRotation = floatQ.Identity;
					});
				}
				return false;
			}
		}
		static bool hasSyncMethod(MethodInfo info) => info.CustomAttributes.Any((a) => a.AttributeType.IsAssignableTo(typeof(SyncMethod)));
		static bool isButtonDelegate(ParameterInfo[] param) => ((param[1].ParameterType == typeof(ButtonEventData) || param[1].ParameterType.BaseType == typeof(ButtonEventData)) && param[0].ParameterType.IsAssignableTo(typeof(IButton)));
		static void actionui(Worker worker, UIBuilder ui, MethodInfo m)
		{
			LocaleString str = m.Name;
			var b = ui.Button(in str);
			var Delegate = StaticSafeCreateDelegate<Action>(m, worker);
			b.Slot.AttachComponent<ButtonActionTrigger>().OnPressed.Target = Delegate;
			proxySource(b, Delegate, typeof(Action));
		}
		static void actionCallbackwitharg(bool isRef, Worker worker, UIBuilder ui, MethodInfo m, ParameterInfo p, Type pt)
		{
			ui.HorizontalLayout();
			LocaleString str = m.Name;
			var b = ui.Button(in str);
			var apt = typeof(Action<>).MakeGenericType(pt);
			Type t = (isRef ? typeof(CallbackRefArgument<>) : typeof(CallbackValueArgument<>)).MakeGenericType(pt);
			var c = b.Slot.AttachComponent(t);
			Type rt = typeof(SyncDelegate<>).MakeGenericType(apt);
			var Delegate = StaticSafeCreateDelegate(m, apt, worker);
			rt.GetProperty("Target").SetValue(t.GetField("Callback").GetValue(c), Delegate);
			proxySource(b, Delegate, apt);
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
			var Delegate = StaticSafeCreateDelegate(m, bpt, worker);
			rt.GetProperty("Target").SetValue(t.GetField("ButtonPressed").GetValue(c), Delegate);
			proxySource(b, Delegate, bpt);
			SyncMemberEditorBuilder.Build(c.GetSyncMember("Argument"), p.Name, t.GetField("Argument"), ui);
			ui.NestOut();
		}
		static void proxySource(Button b, Delegate Delegate, Type delegateType)
		{
			var DSoruceType = typeof(DelegateProxySource<>).MakeGenericType(delegateType);
			var DSrouce = (IDelegateProxySource)b.Slot.AttachComponent(DSoruceType);
			DSrouce.Delegate = Delegate;
			b.Slot.Tag = GrabNodeTag;
		}
		static bool IsMatch(ConfigEntry<String> configEntry, Regex? regex, string input)
		{
			if (RegexDebuging.Value)
			{
				var name = configEntry.Definition.Key;
				log.LogInfo(name + " Regexing: " + input);
				var matches = regex?.Matches(input);
				bool ret = matches?.Count > 0;
				if (ret) log.LogInfo(name + " matched: " + string.Join(" ", matches.Select(e => $"[{string.Join(", ", e.Groups.Values.Select(g => g.Name + " : " + g.Value))}]")));
				return ret;
			}
			else return regex?.IsMatch(input) ?? false;
		}

		void UpdateRegex(ref Regex? regex, ConfigEntry<String> configEntry)
		{
			if (regex?.ToString() != configEntry.Value && configEntry.Value != "")
				try { regex = new(configEntry.Value); }
				catch (RegexParseException re) { Log.LogWarning($"invalid config option {configEntry.Definition.Key}, cannot parse regular expression: {re.Message}"); }
			else
				regex = null;
		}

		static Type GetDelegateNodeType(MethodInfo m, out Type delegateType)
		{
			var isAsync = false;
			var isRetRef = false;
			Type returnType = null;
			return GetDelegateNodeType(m, out isAsync, out isRetRef, out delegateType, out returnType);
		}


		static Type GetDelegateNodeType(MethodInfo m, out bool isAsync, out bool isRetRef, out Type delegateType, out Type returnType)
		{
			isAsync = false;
			isRetRef = false;
			delegateType = null;

			returnType = m.ReturnType;
			Type proxyType;
			var param = m.GetParameters();

			if (param.Any(p => p.ParameterType.IsByRef)) return null;



			if (returnType == typeof(void))
			{
				proxyType = typeof(SyncMethodProxy);
			}
			else if (returnType == typeof(Task))
			{
				proxyType = typeof(AsyncMethodProxy);
				returnType = typeof(void);
				isAsync = true;
			}
			else if (!returnType.IsGenericType || !(returnType.GetGenericTypeDefinition() == typeof(Task<>)))
			{
				isRetRef = !returnType.IsUnmanaged();
				proxyType = isRetRef ? typeof(SyncObjectFunctionProxy<>) : typeof(SyncValueFunctionProxy<>);
			}
			else
			{
				Type t = returnType.GetGenericArguments()[0];
				isRetRef = !t.IsUnmanaged();
				proxyType = isRetRef ? typeof(AsyncObjectFunctionProxy<>) : typeof(AsyncValueFunctionProxy<>);
				returnType = t;
				isAsync = true;
			}

			if (param.Length != 0)
			{
				int paramMask = 0;
				for (int i = 0; i < param.Length; i++)
				{
					if (!param[i].ParameterType.IsUnmanaged())
					{
						paramMask |= 1 << i;
					}
				}
				List<Type> genericArgs = new(param.Select((ParameterInfo p) => p.ParameterType));
				if (returnType != typeof(void))
				{
					genericArgs.Add(returnType);
				}
				Type baseType = proxyType;
				string baseName = baseType.FullName;
				int index = baseName.IndexOf('`');
				if (index >= 0)
				{
					baseName = baseName.Substring(0, index);
				}
				string typename = baseName.Replace("Proxy", $"Proxy_{paramMask:X4}`{genericArgs.Count}");
				proxyType = Type.GetType(typename + ", FrooxEngine");
				if (proxyType == null) return null;
				proxyType = proxyType.MakeGenericType(genericArgs.ToArray());
			}
			else if (proxyType.IsGenericType)
			{
				proxyType = proxyType.MakeGenericType(returnType);
			}
			delegateType = (Type)AccessTools.Property(proxyType, "DelegateType")?.GetValue(null);
			return ProtoFluxHelper.GetBindingForNode(proxyType);
		}
		static Delegate CreateDelegateGlobal(ProtoFluxNode proxyNode, Type delegateType, MethodInfo m, object? target = null)
		{
			Type globalDelegateType = typeof(GlobalDelegate<>).MakeGenericType(delegateType);
			IGlobalValueProxy global = (IGlobalValueProxy)proxyNode.Slot.AttachComponent(globalDelegateType);
			var Delegate = StaticSafeCreateDelegate(m, delegateType, target);
			global.TrySetValue(Delegate);
			proxyNode.GetGlobalRef(0).TrySet(global);
			return Delegate;
		}
		static Delegate StaticSafeCreateDelegate(MethodInfo m, Type delegateType, object? target) => m.CreateDelegate(delegateType, m.IsStatic ? null : target);
		static T StaticSafeCreateDelegate<T>(MethodInfo m, object? target) where T : Delegate => (T)StaticSafeCreateDelegate(m, typeof(T), target);
		static List<MethodInfo> GetAllMethods(Type type)
		{
			var list = new List<MethodInfo>();
			while (type != null) 
			{
				list.AddRange(AccessTools.GetDeclaredMethods(type));
				type = type.BaseType;
			}
			return list;
		}
	}
}