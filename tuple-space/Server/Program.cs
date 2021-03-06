﻿using System;

using MessageService;
using StateMachineReplication;
using StateMachineReplicationAdvanced;
using XuLiskov;
using XuLiskovAdvanced;


namespace Server {
    public static class Program {
        private static readonly log4net.ILog Log = log4net.LogManager.GetLogger(typeof(Program));

        public static void Main(string[] args) {
            if (args.Length != 5) {
                Log.Fatal("Expected: Server server_id URL min_delay max_delay protocol");
                Environment.Exit(1);
            }

            string serverId = args[0];
            Uri url = new Uri(args[1]);
            string protocol = args[4];

            IProtocol protocolToUse = null;
            if (protocol.Equals("SMR")) {
                Log.Info("Using State Machine Replication protocol.");
                protocolToUse = new SMRProtocol();
            } else if (protocol.Equals("SMRA")) {
                Log.Info("Using State Machine Replication Advanced protocol.");
                protocolToUse = new SMRAProtocol();
            } else if (protocol.Equals("XL")) {
                Log.Info("Using Xu-Liskov protocol.");
                protocolToUse = new XLProtocol();
            } else if(protocol.Equals("XLA")) {
                Log.Info("Using Xu-Liskov Advanced protocol.");
                protocolToUse = new XLAProtocol();
            } else {
                Log.Fatal("Unknown protocol.");
                Environment.Exit(1);
            }

            // create message service wrapper
            ServerMessageWrapper serverMessage = new ServerMessageWrapper(
                url,
                protocolToUse,
                int.Parse(args[2]), 
                int.Parse(args[3]));

            // init ProtocolUsed
            protocolToUse.Init(serverMessage.ServiceClient, url, serverId);

            // create puppet master service
            PuppetMasterServer puppetMasterServer = new PuppetMasterServer(protocolToUse, serverMessage);

            Console.ReadLine();
        }
    }
}
