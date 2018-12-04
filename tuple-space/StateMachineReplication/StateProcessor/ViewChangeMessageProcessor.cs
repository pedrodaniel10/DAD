﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MessageService;
using MessageService.Serializable;
using MessageService.Visitor;
using StateMachineReplication.Utils;
using Timeout = MessageService.Timeout;

namespace StateMachineReplication.StateProcessor {
    public class ViewChangeMessageProcessor : IMessageSMRVisitor {
        private static readonly log4net.ILog Log = log4net.LogManager.GetLogger(typeof(ViewChangeMessageProcessor));

        private readonly MessageServiceClient messageServiceClient;
        private readonly ReplicaState replicaState;

        private readonly int viewNumber;
        private readonly SortedDictionary<string, Uri> configuration;
        private readonly bool imTheLeader;

        
        private readonly int numberToWait;

        private int messagesDoViewChange;

        public ViewChangeMessageProcessor(
            MessageServiceClient messageServiceClient, 
            ReplicaState replicaState,
            int viewNumber,
            SortedDictionary<string, Uri> configuration) {
            this.messageServiceClient = messageServiceClient;
            this.replicaState = replicaState;
            this.viewNumber = viewNumber;
            this.configuration = configuration;

            this.imTheLeader = this.configuration.Values.ToArray()[0].Equals(this.replicaState.myUrl);

            this.numberToWait = this.replicaState.Configuration.Count / 2;

            this.messagesDoViewChange = 0;

            Log.Info("Changed to View Change State.");

            // Start the view change protocol
            Task.Factory.StartNew(this.MulticastStartViewChange);
        }

        public ViewChangeMessageProcessor(
            MessageServiceClient messageServiceClient,
            ReplicaState replicaState,
            StartChange startChange) {
            this.messageServiceClient = messageServiceClient;
            this.replicaState = replicaState;

            this.viewNumber = startChange.ViewNumber;
            this.configuration = startChange.Configuration;

            this.imTheLeader = startChange.Configuration.Values.ToArray()[0].Equals(this.replicaState.myUrl);

            this.numberToWait = startChange.Configuration.Count / 2;

            Log.Info("Changed to View Change State.");
        }

        public ViewChangeMessageProcessor(
            MessageServiceClient messageServiceClient,
            ReplicaState replicaState,
            DoViewChange doViewChange) {
            this.messageServiceClient = messageServiceClient;
            this.replicaState = replicaState;

            this.viewNumber = doViewChange.ViewNumber;
            this.configuration = doViewChange.Configuration;

            this.imTheLeader = doViewChange.Configuration.Values.ToArray()[0].Equals(this.replicaState.myUrl);

            this.numberToWait = doViewChange.Configuration.Count / 2;

            Log.Info("Changed to View Change State.");
        }

        public IResponse VisitAddRequest(AddRequest addRequest) {
            return this.WaitNormalState(addRequest);
        }

        public IResponse VisitTakeRequest(TakeRequest takeRequest) {
            return this.WaitNormalState(takeRequest);
        }

        public IResponse VisitReadRequest(ReadRequest readRequest) {
            return this.WaitNormalState(readRequest);
        }

        public IResponse VisitClientHandShakeRequest(ClientHandShakeRequest clientHandShakeRequest) {
            return this.WaitNormalState(clientHandShakeRequest);
        }

        public IResponse VisitServerHandShakeRequest(ServerHandShakeRequest serverHandShakeRequest) {
            return this.WaitNormalState(serverHandShakeRequest);
        }

        public IResponse VisitJoinView(JoinView joinView) {
            return this.WaitNormalState(joinView);
        }

        public IResponse VisitPrepareMessage(PrepareMessage prepareMessage) {
            return this.WaitNormalState(prepareMessage);
        }

        public IResponse VisitCommitMessage(CommitMessage commitMessage) {
            return this.WaitNormalState(commitMessage);
        }

        public IResponse VisitStartViewChange(StartViewChange startViewChange) {
            if (startViewChange.ViewNumber == this.viewNumber &&
                ConfigurationUtils.CompareConfigurations(startViewChange.Configuration, this.configuration)) {
                return new StartViewChangeOk(this.replicaState.ServerId, this.viewNumber, this.configuration);
            }
            Log.Info("Received Start View Change that don't match.");
            return null;
        }

        public IResponse VisitDoViewChange(DoViewChange doViewChange) {
            if (this.imTheLeader &&
                doViewChange.ViewNumber == this.viewNumber &&
                ConfigurationUtils.CompareConfigurations(doViewChange.Configuration, this.configuration) &&
                doViewChange.OldViewNumber == this.replicaState.ViewNumber) {
                Interlocked.Increment(ref this.messagesDoViewChange);
                this.CheckNumberAndSetNewConfiguration();
            }
      
            return null;
        }

        public IResponse VisitStartChange(StartChange startChange) {
            // Set new configuration
            Uri[] replicasUrl = this.configuration.Values
                .Where(url => !url.Equals(this.replicaState.myUrl))
                .ToArray();
            this.replicaState.SetNewConfiguration(this.configuration, replicasUrl, this.viewNumber);
            this.replicaState.ChangeToRecoveryState();
            return null;
        }

        public IResponse VisitRecovery(Recovery recovery) {
            while (!(this.replicaState.State is NormalStateMessageProcessor) && 
                   !(this.replicaState.State is RecoveryStateMessageProcessor)) {
                this.replicaState.HandlerStateChanged.WaitOne();
            }
            return recovery.Accept(this.replicaState.State);
        }

        private IResponse WaitNormalState(IMessage message) {
            while (!(this.replicaState.State is NormalStateMessageProcessor)) {
                this.replicaState.HandlerStateChanged.WaitOne();
            }
            return message.Accept(this.replicaState.State);
        }

        private void MulticastStartViewChange() {
            IMessage message = new StartViewChange(this.replicaState.ServerId, this.viewNumber, this.configuration);
            Uri[] currentConfiguration = this.replicaState.ReplicasUrl.ToArray();

            IResponses responses = this.messageServiceClient.RequestMulticast(
                message,
                currentConfiguration,
                this.replicaState.Configuration.Count / 2,
                (int)(Timeout.TIMEOUT_HEART_BEAT * 2));

            IResponse[] responsesVector = new List<IResponse>(responses.ToArray())
                .Where(response => response != null)
                .ToArray();

            // There was no quorum to accept the view change
            if (responsesVector.Length < this.numberToWait) {
                Log.Info($"There was no quorum for view change. " +
                         $"Just received {responsesVector.Length} from at least {this.numberToWait}");
                this.replicaState.ChangeToNormalState();
                return;
            }

            // In case I'm the leader, wait for f DoViewChange
            if (this.imTheLeader) {
                this.CheckNumberAndSetNewConfiguration();
            } else {
                // Else, send DoViewChange to leader
                Uri leader = this.configuration.Values.ToArray()[0];
                IMessage doViewMessage = new DoViewChange(
                    this.replicaState.ServerId,
                    this.viewNumber,
                    this.replicaState.ViewNumber,
                    this.configuration);

                this.messageServiceClient.Request(doViewMessage, leader, -1);
            }

            Thread.Sleep(Timeout.TIMEOUT_HEART_BEAT);
            if (this.Equals(this.replicaState.State)) {
                // View Change was not successful, return to normal
                Log.Info("View Change was not successful.");
                this.replicaState.ChangeToNormalState();
            }
        }


        private void CheckNumberAndSetNewConfiguration() {
            if (this.messagesDoViewChange >= this.numberToWait) {
                // start change
                Uri[] replicasUrl = this.configuration.Values
                    .Where(url => !url.Equals(this.replicaState.myUrl))
                    .ToArray();

                IMessage message = new StartChange(this.replicaState.ServerId, this.viewNumber, this.configuration);
                Task.Factory.StartNew(() => 
                    this.messageServiceClient.RequestMulticast(message, replicasUrl, replicasUrl.Length, -1));

                // Set new configuration
                this.replicaState.SetNewConfiguration(this.configuration, replicasUrl, this.viewNumber);

                this.replicaState.ChangeToRecoveryState();
            }
        }

        public override string ToString() {
            return "View Change";
        }
    }
}