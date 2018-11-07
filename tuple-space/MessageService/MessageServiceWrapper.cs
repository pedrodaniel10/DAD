﻿using System;
using System.Reflection;
using System.Runtime.Remoting.Channels;
using System.Runtime.Remoting.Channels.Tcp;

using log4net;

namespace MessageService {
    public class MessageServiceWrapper {
        private static readonly log4net.ILog Log = log4net.LogManager.GetLogger(typeof(MessageServiceWrapper));

        private readonly TcpChannel channel;

        public MessageServiceServer ServiceServer { get; }

        public MessageServiceClient ServiceClient { get; }

        public MessageServiceWrapper(Uri myUrl, IProtocol protocol, int minDelay, int maxDelay) {
            // create tcp channel
            this.channel = new TcpChannel(myUrl.Port);
            ChannelServices.RegisterChannel(this.channel, false);
            Log.Info("TCP channel created.");

            // create MessageServiceServer
            this.ServiceServer = new MessageServiceServer(protocol, minDelay, maxDelay);

            // create MessageServiceClient
            this.ServiceClient = new MessageServiceClient(this.channel);
        }

        public void Freeze() {
            this.ServiceClient.Freeze();
            this.ServiceServer.Freeze();
        }

        public void Unfreeze() {
            this.ServiceClient.Unfreeze();
            this.ServiceServer.Unfreeze();
        }
    }
}