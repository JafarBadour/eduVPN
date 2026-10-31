/*
    eduVPN - VPN for education and research

    Copyright: 2017-2024 The Commons Conservancy
    SPDX-License-Identifier: GPL-3.0+
*/

using eduVPN.ViewModels.Windows;
using System.Diagnostics;

namespace eduVPN.ViewModels.Pages
{
    /// <summary>
    /// Settings wizard page
    /// </summary>
    public class SettingsPage : ConnectWizardPopupPage
    {
        #region Properties

        /// <summary>
        /// Client-side split tunneling settings
        /// </summary>
        public SplitTunnelSettingsViewModel SplitTunnel
        {
            get
            {
                if (_SplitTunnel == null)
                    _SplitTunnel = new SplitTunnelSettingsViewModel(Wizard);
                return _SplitTunnel;
            }
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private SplitTunnelSettingsViewModel _SplitTunnel;

        #endregion

        #region Constructors

        /// <summary>
        /// Constructs a settings wizard page.
        /// </summary>
        /// <param name="wizard">The connecting wizard</param>
        public SettingsPage(ConnectWizard wizard) :
            base(wizard)
        {
        }

        #endregion

        #region Methods

        /// <inheritdoc/>
        public override void OnActivate()
        {
            base.OnActivate();
            // The server list and the connection state may have changed since the page was built.
            SplitTunnel.Refresh();
        }

        #endregion
    }
}
