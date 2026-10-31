/*
    eduVPN - VPN for education and research

    Copyright: 2024-2026 The Commons Conservancy
    SPDX-License-Identifier: GPL-3.0+
*/

using eduVPN.Models;
using eduVPN.ViewModels.VPN;
using eduVPN.ViewModels.Windows;
using Prism.Commands;
using Prism.Mvvm;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;

namespace eduVPN.ViewModels.Pages
{
    /// <summary>
    /// Backs the split tunneling group of the settings page
    /// </summary>
    /// <remarks>
    /// The group edits the settings of exactly one server at a time; the user picks which one.
    /// Edits are written to <see cref="Properties.Settings"/> as they are made, which is how the
    /// other settings on this page behave too.
    /// </remarks>
    public class SplitTunnelSettingsViewModel : BindableBase
    {
        #region Fields

        private readonly ConnectWizard Wizard;

        #endregion

        #region Properties

        /// <summary>
        /// Every server the user added
        /// </summary>
        public ObservableCollection<Server> Servers { get; } = new ObservableCollection<Server>();

        /// <summary>
        /// The server whose settings are shown
        /// </summary>
        public Server SelectedServer
        {
            get => _SelectedServer;
            set
            {
                if (SetProperty(ref _SelectedServer, value))
                    Load();
            }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private Server _SelectedServer;

        /// <summary>
        /// What to do with the routes the server pushed
        /// </summary>
        public SplitTunnelMode Mode
        {
            get => _Settings.Mode;
            set
            {
                if (_Settings.Mode == value)
                    return;
                if (value != SplitTunnelMode.Off && !_Settings.PolicyWarningAcknowledged && IsDefaultGateway)
                {
                    // The warning is shown once per server, and only the user can accept it.
                    PolicyWarningPending = true;
                    RaisePropertyChanged(nameof(PolicyWarningPending));
                }
                _Settings.Mode = value;
                Save();
                RaisePropertyChanged();
                RaisePropertyChanged(nameof(IsSplitTunnelEnabled));
            }
        }

        /// <summary>
        /// What to do with the DNS servers the server pushed
        /// </summary>
        public SplitTunnelDnsMode Dns
        {
            get => _Settings.Dns;
            set
            {
                if (_Settings.Dns == value)
                    return;
                _Settings.Dns = value;
                Save();
                RaisePropertyChanged();
            }
        }

        /// <summary>
        /// Whether the route and DNS editors apply at all
        /// </summary>
        public bool IsSplitTunnelEnabled => _Settings.Mode != SplitTunnelMode.Off;

        /// <summary>
        /// Whether the settings are dictated by the registry and cannot be changed here
        /// </summary>
        public bool IsManaged => Properties.SettingsEx.Default.IsSplitTunnelManaged;

        /// <summary>
        /// Whether the user may change the settings
        /// </summary>
        public bool IsEditable => !IsManaged;

        /// <summary>
        /// The networks the user listed
        /// </summary>
        public ObservableCollection<string> Routes { get; } = new ObservableCollection<string>();

        /// <summary>
        /// The network the user is typing
        /// </summary>
        public string NewRoute
        {
            get => _NewRoute;
            set
            {
                if (SetProperty(ref _NewRoute, value))
                {
                    RouteError = null;
                    _AddRoute?.RaiseCanExecuteChanged();
                }
            }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private string _NewRoute;

        /// <summary>
        /// The network the user selected in the list
        /// </summary>
        public string SelectedRoute
        {
            get => _SelectedRoute;
            set
            {
                if (SetProperty(ref _SelectedRoute, value))
                    _RemoveRoute?.RaiseCanExecuteChanged();
            }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private string _SelectedRoute;

        /// <summary>
        /// Why the typed network was rejected, or <c>null</c> when there is nothing to report
        /// </summary>
        public string RouteError
        {
            get => _RouteError;
            private set => SetProperty(ref _RouteError, value);
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private string _RouteError;

        /// <summary>
        /// Whether the selected server handed out a default-gateway profile
        /// </summary>
        /// <remarks>The flag only reaches the client together with a configuration, so a server we
        /// never connected to is assumed to be default-gateway: every eduVPN profile routes
        /// everything unless the administrator says otherwise.</remarks>
        public bool IsDefaultGateway =>
            SelectedServer == null ||
            Wizard.ConnectionPage.ActiveSession?.Server?.Id != SelectedServer.Id ||
            Wizard.ConnectionPage.ActiveSession.IsDefaultGateway;

        /// <summary>
        /// Whether the policy warning still has to be shown to the user
        /// </summary>
        public bool PolicyWarningPending { get; private set; }

        /// <summary>
        /// Whether the selected server is connected right now, so the changes only take effect
        /// after reconnecting
        /// </summary>
        public bool NeedsReconnect =>
            SelectedServer != null &&
            Wizard.ConnectionPage.ActiveSession?.Server?.Id == SelectedServer.Id;

        /// <summary>
        /// Records that the user accepted the policy warning for the selected server
        /// </summary>
        public DelegateCommand AcceptPolicyWarning
        {
            get
            {
                if (_AcceptPolicyWarning == null)
                    _AcceptPolicyWarning = new DelegateCommand(
                        () =>
                        {
                            PolicyWarningPending = false;
                            _Settings.PolicyWarningAcknowledged = true;
                            Save();
                            RaisePropertyChanged(nameof(PolicyWarningPending));
                        });
                return _AcceptPolicyWarning;
            }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private DelegateCommand _AcceptPolicyWarning;

        /// <summary>
        /// Reverts to <see cref="SplitTunnelMode.Off"/> after the user declined the policy warning
        /// </summary>
        public DelegateCommand DeclinePolicyWarning
        {
            get
            {
                if (_DeclinePolicyWarning == null)
                    _DeclinePolicyWarning = new DelegateCommand(
                        () =>
                        {
                            PolicyWarningPending = false;
                            _Settings.Mode = SplitTunnelMode.Off;
                            Save();
                            RaisePropertyChanged(nameof(PolicyWarningPending));
                            RaisePropertyChanged(nameof(Mode));
                            RaisePropertyChanged(nameof(IsSplitTunnelEnabled));
                        });
                return _DeclinePolicyWarning;
            }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private DelegateCommand _DeclinePolicyWarning;

        /// <summary>
        /// Adds <see cref="NewRoute"/> to <see cref="Routes"/>
        /// </summary>
        public DelegateCommand AddRoute
        {
            get
            {
                if (_AddRoute == null)
                    _AddRoute = new DelegateCommand(
                        () =>
                        {
                            if (!IPPrefixMath.TryParseRoute(NewRoute, out var prefix))
                            {
                                RouteError = Resources.Strings.SplitTunnelRouteInvalid;
                                return;
                            }
                            if (Routes.Count >= SplitTunnelSettings.MaxRoutes)
                            {
                                RouteError = Resources.Strings.SplitTunnelTooManyRoutes;
                                return;
                            }
                            var canonical = IPPrefixMath.ToCidrString(prefix);
                            if (!Routes.Contains(canonical))
                                Routes.Add(canonical);
                            NewRoute = null;
                            RouteError = null;
                            Save();
                        },
                        () => !string.IsNullOrWhiteSpace(NewRoute));
                return _AddRoute;
            }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private DelegateCommand _AddRoute;

        /// <summary>
        /// Removes <see cref="SelectedRoute"/> from <see cref="Routes"/>
        /// </summary>
        public DelegateCommand RemoveRoute
        {
            get
            {
                if (_RemoveRoute == null)
                    _RemoveRoute = new DelegateCommand(
                        () =>
                        {
                            Routes.Remove(SelectedRoute);
                            Save();
                        },
                        () => SelectedRoute != null);
                return _RemoveRoute;
            }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private DelegateCommand _RemoveRoute;

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private SplitTunnelSettings _Settings = SplitTunnelSettings.Off.Clone();

        #endregion

        #region Constructors

        /// <summary>
        /// Constructs the view model.
        /// </summary>
        /// <param name="wizard">The connecting wizard</param>
        public SplitTunnelSettingsViewModel(ConnectWizard wizard)
        {
            Wizard = wizard;
            Refresh();
        }

        #endregion

        #region Methods

        /// <summary>
        /// Rebuilds the server list and reloads the settings of the selected server.
        /// </summary>
        public void Refresh()
        {
            var previous = SelectedServer?.Id;
            Servers.Clear();
            foreach (var server in Wizard.HomePage.InstituteAccessServers.Cast<Server>()
                .Concat(Wizard.HomePage.SecureInternetServers)
                .Concat(Wizard.HomePage.OwnServers))
                Servers.Add(server);
            SelectedServer =
                Servers.FirstOrDefault(s => s.Id == previous) ??
                Servers.FirstOrDefault(s => s.Id == Wizard.ConnectionPage.ActiveSession?.Server?.Id) ??
                Servers.FirstOrDefault();
            Load();
        }

        private void Load()
        {
            // Read the effective settings, which the registry may dictate.
            _Settings = SelectedServer != null
                ? Properties.SettingsEx.Default.SplitTunnelSettings.Get(SelectedServer.Id).Clone()
                : SplitTunnelSettings.Off.Clone();
            Routes.Clear();
            foreach (var route in _Settings.Routes)
                Routes.Add(route);
            RouteError = null;
            PolicyWarningPending = false;
            RaisePropertyChanged(nameof(Mode));
            RaisePropertyChanged(nameof(Dns));
            RaisePropertyChanged(nameof(IsSplitTunnelEnabled));
            RaisePropertyChanged(nameof(IsManaged));
            RaisePropertyChanged(nameof(IsEditable));
            RaisePropertyChanged(nameof(IsDefaultGateway));
            RaisePropertyChanged(nameof(NeedsReconnect));
            RaisePropertyChanged(nameof(PolicyWarningPending));
        }

        private void Save()
        {
            if (SelectedServer == null || IsManaged)
                return;
            _Settings.Routes = Routes.ToList();
            Properties.Settings.Default.SplitTunnelSettings.Set(SelectedServer.Id, _Settings);
            RaisePropertyChanged(nameof(NeedsReconnect));
        }

        #endregion
    }
}
