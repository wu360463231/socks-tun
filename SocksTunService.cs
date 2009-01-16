﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Org.Mentalis.Network.ProxySocket;
using SocksTun.Properties;

namespace SocksTun
{
	public partial class SocksTunService : ServiceBase
	{
		public SocksTunService()
		{
			InitializeComponent();
			debug.LogLevel = 2;
		}

		public void Run(string[] args)
		{
			Console.CancelKeyPress += Console_CancelKeyPress;
			debug.Writer = Console.Out;
			OnStart(args);
			debug.Log(-1, "SocksTun running in foreground mode, press enter to exit");
			Console.ReadLine();
			debug.Log(-1, "Shutting down...");
			OnStop();
		}

		void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
		{
			e.Cancel = true;
		}

		private readonly DebugWriter debug = new DebugWriter();
		private readonly EventWaitHandle stoppedEvent = new ManualResetEvent(false);

		private TunTapDevice tunTapDevice;
		private FileStream tap;

		private ConnectionTracker connectionTracker;
		private Natter natter;

		private TcpListener transparentSocksServer;
		private TcpListener logServer;

		protected override void OnStart(string[] args)
		{
			transparentSocksServer = new TcpListener(IPAddress.Any, Settings.Default.SocksPort);
			transparentSocksServer.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
			transparentSocksServer.Start();
			debug.Log(0, "TransparentSocksPort = " + ((IPEndPoint)transparentSocksServer.LocalEndpoint).Port);
			transparentSocksServer.BeginAcceptSocket(NewTransparentSocksConnection, null);

			logServer = new TcpListener(IPAddress.Loopback, Settings.Default.LogPort);
			logServer.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, 1);
			logServer.Start();
			debug.Log(0, "LogPort = " + ((IPEndPoint) logServer.LocalEndpoint).Port);
			logServer.BeginAcceptTcpClient(NewLogConnection, null);

			tunTapDevice = new TunTapDevice(null);
			debug.Log(0, "Name = " + tunTapDevice.Name);
			debug.Log(0, "Guid = " + tunTapDevice.Guid.ToString("B"));
			debug.Log(0, "Mac = " + tunTapDevice.GetMac());
			debug.Log(0, "Version = " + tunTapDevice.GetVersion());
			debug.Log(0, "Mtu = " + tunTapDevice.GetMtu());

			var localIP = IPAddress.Parse(Settings.Default.IPAddress);
			var remoteNetwork = IPAddress.Parse(Settings.Default.RemoteNetwork);
			var remoteNetmask = IPAddress.Parse(Settings.Default.RemoteNetmask);
			tunTapDevice.ConfigTun(localIP, remoteNetwork, remoteNetmask);

			var adapterNetmask = IPAddress.Parse(Settings.Default.DHCPNetmask);
			var dhcpServerAddr = IPAddress.Parse(Settings.Default.DHCPServer);
			var dhcpLeaseTime = Settings.Default.DHCPLeaseTime;
			tunTapDevice.ConfigDhcpMasq(localIP, adapterNetmask, dhcpServerAddr, dhcpLeaseTime);

			tunTapDevice.ConfigDhcpSetOptions(
				new DhcpOption.Routers(
					dhcpServerAddr
				),
				new DhcpOption.VendorOptions(
					new DhcpVendorOption.NetBIOSOverTCP(2)
				)
			);

			tunTapDevice.SetMediaStatus(true);

			tap = tunTapDevice.Stream;
			connectionTracker = new ConnectionTracker();
			natter = new Natter(tap, debug, connectionTracker, ((IPEndPoint)transparentSocksServer.LocalEndpoint).Port);

			natter.BeginRun(NatterStopped, null);
		}

		protected override void OnStop()
		{
			natter.Stop();
			tap.Close();
			connectionTracker.Dispose();
			stoppedEvent.WaitOne();
		}

		private void NatterStopped(IAsyncResult ar)
		{
			natter.EndRun(ar);
			stoppedEvent.Set();
		}

		private void NewTransparentSocksConnection(IAsyncResult ar)
		{
			Socket client = null;
			try
			{
				client = transparentSocksServer.EndAcceptSocket(ar);
			}
			catch (SystemException)
			{
			}
			transparentSocksServer.BeginAcceptSocket(NewTransparentSocksConnection, null);

			if (client == null) return;
			var connection = new TransparentSocksConnection(client, debug, connectionTracker, ConfigureSocksProxy);
			connection.Process();
		}

		private static void ConfigureSocksProxy(ProxySocket proxySocket, IPEndPoint requestedEndPoint)
		{
			// TODO: Make this configurable
			proxySocket.ProxyType = ProxyTypes.Socks5;
			proxySocket.ProxyEndPoint = new IPEndPoint(IPAddress.Loopback, requestedEndPoint.Port == 443 ? 8000 : 1080);
		}

		private void NewLogConnection(IAsyncResult ar)
		{
			try
			{
				var client = logServer.EndAcceptTcpClient(ar);

				var connection = new LogConnection(client, debug, connectionTracker, tunTapDevice);
				connection.Process();
			}
			catch (SystemException)
			{
			}

			logServer.BeginAcceptTcpClient(NewLogConnection, null);
		}
	}
}
