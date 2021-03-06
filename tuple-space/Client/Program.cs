﻿using System;
using System.Linq;
using Client.Exceptions;
using Client.Visitor;

using MessageService;
using MessageService.Serializable;

namespace Client {
    public static class Program {
        private static readonly log4net.ILog Log = log4net.LogManager.GetLogger(typeof(Program));

        

        static void Main(string[] args) {
            try {
                //TODO check arguments
                Client client = new Client(args[0], new Uri(args[1]), args[2]);

                ClientHandShakeResponse response = client.DoHandShake();
                
                switch (response.ProtocolUsed) {
                    case Protocol.StateMachineReplication:
                        Log.Info("Handshake: Using State Machine Replication protocol.");
                        client.Script.Accept(new SMRExecuter(client.MessageServiceClient, client));
                        break;
                    case Protocol.XuLiskov:
                        Log.Info("Handshake: Using Xu-Liskov protocol");
                        client.Script.Accept(v: new XLExecuter(client.MessageServiceClient, client));
                        break;
                    default:
                        Log.Fatal("Unknown protocol.");
                        Environment.Exit(1);
                        break;
                }

                Console.ReadLine();
            } catch (Exception ex) {
                if (ex is IncorrectCommandException || ex is BlockEndMissingException) {
                    Console.WriteLine(ex.Message);
                } else {
                    throw;
                }
            }
        }
    }
}