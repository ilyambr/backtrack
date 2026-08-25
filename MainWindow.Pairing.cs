using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Interop;
using Backtrack.Obs;
using Backtrack.Pairing;
using Backtrack.Streaming;
using Backtrack.Updates;
using Microsoft.Win32;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{

    

        private void RefreshShareClipsUi()
    {
        bool hasAuthorizedDevice = !string.IsNullOrEmpty(_settings.AuthorizedClientName);

        if (!_settings.ShareClipsEnabled)
        {
            ShareClipsStatusText.Text = "Off";
        }
        else if (hasAuthorizedDevice)
        {
            ShareClipsStatusText.Text = $"Sharing as \"{Environment.MachineName}\"";
        }
        else
        {
            ShareClipsStatusText.Text = $"Sharing as \"{Environment.MachineName}\", waiting for a PC to pair";
        }

        
        
        
        
        
        
        AuthorizedDeviceRow.Visibility = _settings.ShareClipsEnabled && hasAuthorizedDevice ? Visibility.Visible : Visibility.Collapsed;
        AuthorizedDeviceNameText.Text = _settings.AuthorizedClientName ?? "";
    }


    private void DeauthorizeButton_Click(object sender, RoutedEventArgs e)
    {
        string? name = _settings.AuthorizedClientName;
        if (name is null)
            return;

        if (MessageBox.Show(this, $"Remove \"{name}\"'s access to this PC's clips? It'll need to pair again to reconnect.",
                "Backtrack", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            return;

        _settings.AuthorizedClientDeviceId = null;
        _settings.AuthorizedClientName = null;
        _settings.AuthorizedClientSecret = null;
        _settings.Save();
        RefreshShareClipsUi();
    }


    private void ShareClipsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = ShareClipsToggle.IsChecked == true;
        _settings.ShareClipsEnabled = enabled;
        _settings.Save();

        if (enabled)
        {
            _pairing.StartAnnouncing();
            _pairing.StartPairingServer();
        }
        else
        {
            _pairing.StopAnnouncing();
            _pairing.StopPairingServer();
        }

        RefreshShareClipsUi();
    }


    private void UnpairButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.PairedPeerDeviceId = null;
        _settings.PairedPeerName = null;
        _settings.PairedPeerHost = null;
        _settings.PairedPeerPort = 0;
        _settings.PairedPeerSecret = null;
        _settings.Save();
        RefreshPairingStatusUi();
    }


        private void RenderDiscoveredDevices()
    {
        DiscoveredDevicesPanel.Children.Clear();

        if (!string.IsNullOrEmpty(_settings.PairedPeerName))
            return; 

        var peers = _pairing.DiscoveredPeers;
        if (peers.Count == 0)
        {
            DiscoveredDevicesPanel.Children.Add(new TextBlock
            {
                Text = "No other Backtrack PCs found on this network yet. Make sure the other PC has \"Share my clips\" turned on.",
                FontSize = 10.5,
                Foreground = (Brush)FindResource("Text2"),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }

        foreach (DiscoveredPeer peer in peers)
            DiscoveredDevicesPanel.Children.Add(BuildDiscoveredDeviceRow(peer));
    }


    private Border BuildDiscoveredDeviceRow(DiscoveredPeer peer)
    {
        var name = new TextBlock { Text = peer.DeviceName, FontWeight = FontWeights.Bold, FontSize = 12, Foreground = (Brush)FindResource("Text0"), VerticalAlignment = VerticalAlignment.Center };
        var statusText = new TextBlock { Text = "", FontSize = 10.5, Foreground = (Brush)FindResource("Text2"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
        var pairButton = new Button { Content = "Pair", Style = (Style)FindResource("IconButton"), Margin = new Thickness(0) };

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(name);
        left.Children.Add(statusText);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(left, 0);
        Grid.SetColumn(pairButton, 1);
        row.Children.Add(left);
        row.Children.Add(pairButton);

        pairButton.Click += async (_, _) =>
        {
            pairButton.IsEnabled = false;
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(70));
            try
            {
                PairingResult result = await _pairing.RequestPairingAsync(peer,
                    onCodeReceived: code => Dispatcher.BeginInvoke(() => statusText.Text = $"Code: {code}, waiting for approval..."),
                    cts.Token);

                switch (result.Outcome)
                {
                    case PairingOutcome.Approved:
                        statusText.Text = "Paired!";
                        RefreshPairingStatusUi();
                        RenderDiscoveredDevices();
                        return;
                    case PairingOutcome.Denied:
                        statusText.Text = string.IsNullOrEmpty(result.Error) ? "Request denied." : result.Error;
                        break;
                    case PairingOutcome.TimedOut:
                        statusText.Text = "Request timed out.";
                        break;
                    default:
                        statusText.Text = $"Failed: {result.Error}";
                        break;
                }
            }
            finally
            {
                pairButton.IsEnabled = true;
            }
        };

        return new Border { BorderBrush = (Brush)FindResource("Hairline"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 8, 0, 8), Child = row };
    }

}
