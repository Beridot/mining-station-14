using Content.Server.Atmos;
using Content.Server.Atmos.EntitySystems;
using Content.Server.Atmos.Piping.Components;
using Content.Server.Power.Components;
using Content.Server.Hands.Components;
using Content.Server.Administration.Logs;
using Content.Shared.Examine;
using Content.Shared.Power;
using Content.Shared.Verbs;
using Content.Shared.Database;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Content.Server.Administration.Managers;
using Content.Shared.Administration;
using Content.Shared.CCVar;
using Robust.Shared.Configuration;

namespace Content.Server.Power.EntitySystems
{
    public sealed class PowerReceiverSystem : EntitySystem
    {
        [Dependency] private readonly IAdminLogManager _adminLogger = default!;
        [Dependency] private readonly IAdminManager _adminManager = default!;
        [Dependency] private readonly AppearanceSystem _appearance = default!;
        [Dependency] private readonly AudioSystem _audio = default!;
        [Dependency] private readonly AtmosphereSystem _atmosphereSystem = default!;
        [Dependency] private readonly IConfigurationManager _cfg = default!;

        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<ApcPowerReceiverComponent, ExaminedEvent>(OnExamined);

            SubscribeLocalEvent<ApcPowerReceiverComponent, ExtensionCableSystem.ProviderConnectedEvent>(OnProviderConnected);
            SubscribeLocalEvent<ApcPowerReceiverComponent, ExtensionCableSystem.ProviderDisconnectedEvent>(OnProviderDisconnected);

            SubscribeLocalEvent<ApcPowerReceiverComponent, AtmosDeviceUpdateEvent>(OnAtmosUpdate);

            SubscribeLocalEvent<ApcPowerProviderComponent, ComponentShutdown>(OnProviderShutdown);
            SubscribeLocalEvent<ApcPowerProviderComponent, ExtensionCableSystem.ReceiverConnectedEvent>(OnReceiverConnected);
            SubscribeLocalEvent<ApcPowerProviderComponent, ExtensionCableSystem.ReceiverDisconnectedEvent>(OnReceiverDisconnected);

            SubscribeLocalEvent<ApcPowerReceiverComponent, GetVerbsEvent<Verb>>(OnGetVerbs);
            SubscribeLocalEvent<PowerSwitchComponent, GetVerbsEvent<AlternativeVerb>>(AddSwitchPowerVerb);
        }

        private void OnGetVerbs(EntityUid uid, ApcPowerReceiverComponent component, GetVerbsEvent<Verb> args)
        {
            if (!_adminManager.HasAdminFlag(args.User, AdminFlags.Admin))
                return;

            // add debug verb to toggle power requirements
            args.Verbs.Add(new()
            {
                Text = Loc.GetString("verb-debug-toggle-need-power"),
                Category = VerbCategory.Debug,
                IconTexture = "/Textures/Interface/VerbIcons/smite.svg.192dpi.png", // "smite" is a lightning bolt
                Act = () => component.NeedsPower = !component.NeedsPower
            });
        }

        ///<summary>
        ///Adds some markup to the examine text of whatever object is using this component to tell you if it's powered or not, even if it doesn't have an icon state to do this for you.
        ///</summary>
        private void OnExamined(EntityUid uid, ApcPowerReceiverComponent component, ExaminedEvent args)
        {
            string state;
            if (component.Powered)
                state = "power-receiver-component-on-examine-powered";
            else if (component.PowerDisabled)
                state = "power-receiver-component-on-examine-off";
            else
                state = "power-receiver-component-on-examine-unpowered";
            args.PushMarkup(Loc.GetString("power-receiver-component-on-examine-main",
                                            ("stateText", Loc.GetString(state))));
        }

        private void OnProviderShutdown(EntityUid uid, ApcPowerProviderComponent component, ComponentShutdown args)
        {
            foreach (var receiver in component.LinkedReceivers)
            {
                receiver.NetworkLoad.LinkedNetwork = default;
                component.Net?.QueueNetworkReconnect();
            }

            component.LinkedReceivers.Clear();
        }

        private void OnProviderConnected(EntityUid uid, ApcPowerReceiverComponent receiver, ExtensionCableSystem.ProviderConnectedEvent args)
        {
            var providerUid = args.Provider.Owner;
            if (!EntityManager.TryGetComponent<ApcPowerProviderComponent>(providerUid, out var provider))
                return;

            receiver.Provider = provider;

            ProviderChanged(receiver);
        }

        private void OnProviderDisconnected(EntityUid uid, ApcPowerReceiverComponent receiver, ExtensionCableSystem.ProviderDisconnectedEvent args)
        {
            receiver.Provider = null;

            ProviderChanged(receiver);
        }

        private void OnReceiverConnected(EntityUid uid, ApcPowerProviderComponent provider, ExtensionCableSystem.ReceiverConnectedEvent args)
        {
            if (EntityManager.TryGetComponent(args.Receiver.Owner, out ApcPowerReceiverComponent? receiver))
            {
                provider.AddReceiver(receiver);
            }
        }

        private void OnReceiverDisconnected(EntityUid uid, ApcPowerProviderComponent provider, ExtensionCableSystem.ReceiverDisconnectedEvent args)
        {
            if (EntityManager.TryGetComponent(args.Receiver.Owner, out ApcPowerReceiverComponent? receiver))
            {
                provider.RemoveReceiver(receiver);
            }
        }

        private void AddSwitchPowerVerb(EntityUid uid, PowerSwitchComponent component, GetVerbsEvent<AlternativeVerb> args)
        {
            if(!args.CanAccess || !args.CanInteract)
                return;

            if (!HasComp<HandsComponent>(args.User))
                return;

            if (!TryComp<ApcPowerReceiverComponent>(uid, out var receiver))
                return;

            if (!receiver.NeedsPower)
                return;

            AlternativeVerb verb = new()
            {
                Act = () =>
                {
                    TogglePower(uid, user: args.User);
                },
                IconTexture = "/Textures/Interface/VerbIcons/Spare/poweronoff.svg.192dpi.png",
                Text = Loc.GetString("power-switch-component-toggle-verb"),
                Priority = -3
            };
            args.Verbs.Add(verb);
        }

        private void ProviderChanged(ApcPowerReceiverComponent receiver)
        {
            receiver.NetworkLoad.LinkedNetwork = default;
            var ev = new PowerChangedEvent(receiver.Powered, receiver.NetworkLoad.ReceivingPower);

            RaiseLocalEvent(receiver.Owner, ref ev);
            _appearance.SetData(receiver.Owner, PowerDeviceVisuals.Powered, receiver.Powered);
        }

        /// <summary>
        /// If this takes power, it returns whether it has power.
        /// Otherwise, it returns 'true' because if something doesn't take power
        /// it's effectively always powered.
        /// </summary>
        public bool IsPowered(EntityUid uid, ApcPowerReceiverComponent? receiver = null)
        {
            if (!Resolve(uid, ref receiver, false))
                return true;

            return receiver.Powered;
        }

        /// <summary>
        /// Turn this machine on or off.
        /// Returns true if we turned it on, false if we turned it off.
        /// </summary>
        public bool TogglePower(EntityUid uid, bool playSwitchSound = true, ApcPowerReceiverComponent? receiver = null, EntityUid? user = null)
        {
            if (!Resolve(uid, ref receiver, false))
                return true;

            // it'll save a lot of confusion if 'always powered' means 'always powered'
            if (!receiver.NeedsPower)
            {
                receiver.PowerDisabled = false;
                return true;
            }

            receiver.PowerDisabled = !receiver.PowerDisabled;

            if (user != null)
                _adminLogger.Add(LogType.Action, LogImpact.Low, $"{ToPrettyString(user.Value):player} hit power button on {ToPrettyString(uid)}, it's now {(!receiver.PowerDisabled ? "on" : "off")}");

            if (playSwitchSound)
            {
                _audio.PlayPvs(new SoundPathSpecifier("/Audio/Machines/machine_switch.ogg"), uid,
                    AudioParams.Default.WithVolume(-2f));
            }

            return !receiver.PowerDisabled; // i.e. PowerEnabled
        }

        private void OnAtmosUpdate(EntityUid uid, ApcPowerReceiverComponent comp, AtmosDeviceUpdateEvent args)
        {
            if (!IsPowered(uid, comp))
                return;

            var dumpHeatEnabled = _cfg.GetCVar(CCVars.DumpHeat);
            if (!dumpHeatEnabled)
                return;

            var environment = _atmosphereSystem.GetContainingMixture(uid, true, true);
            if (environment is null)
                return;
            float dt = 1/_atmosphereSystem.AtmosTickRate;
            float dQ = comp.Load * comp.DumpHeat * dt;
            _atmosphereSystem.AddHeat(environment, dQ);
        }
    }
}
