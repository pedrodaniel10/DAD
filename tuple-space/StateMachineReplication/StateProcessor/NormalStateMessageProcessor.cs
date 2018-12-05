﻿
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MessageService;
using MessageService.Serializable;
using MessageService.Visitor;
using Timeout = MessageService.Timeout;

namespace StateMachineReplication.StateProcessor {
    internal enum ProcessRequest { DROP, LAST_EXECUTION}

    public class NormalStateMessageProcessor : IMessageSMRVisitor {
        private static readonly log4net.ILog Log = log4net.LogManager.GetLogger(typeof(NormalStateMessageProcessor));

        private readonly MessageServiceClient messageServiceClient;
        private readonly ReplicaState replicaState;

        public NormalStateMessageProcessor(ReplicaState replicaState, MessageServiceClient messageServiceClient) {
            this.messageServiceClient = messageServiceClient;
            this.replicaState = replicaState;

            Log.Info("Changed to Normal State.");
        }

        public IResponse VisitAddRequest(AddRequest addRequest) {
            if (!this.replicaState.IAmTheLeader()) {
                // I'm not the leader. 
                // TODO: return who's the leader
                return null;
            }

            ProcessRequest runProcessRequestProtocol = this.RunProcessRequestProtocol(addRequest);
            if (runProcessRequestProtocol == ProcessRequest.DROP) {
                return null;
            }

            if (runProcessRequestProtocol == ProcessRequest.LAST_EXECUTION) {
                return this.replicaState.ClientTable[addRequest.ClientId].Item2;
            }

            return null;
        }

        public IResponse VisitTakeRequest(TakeRequest takeRequest) {
            if (!this.replicaState.IAmTheLeader()) {
                // I'm not the leader. 
                // TODO: return who's the leader
                return null;
            }

            ProcessRequest runProcessRequestProtocol = this.RunProcessRequestProtocol(takeRequest);
            if (runProcessRequestProtocol == ProcessRequest.DROP) {
                return null;
            }

            if (runProcessRequestProtocol == ProcessRequest.LAST_EXECUTION) {
                return this.replicaState.ClientTable[takeRequest.ClientId].Item2;
            }

            return null;
        }

        public IResponse VisitReadRequest(ReadRequest readRequest) {
            if (!this.replicaState.IAmTheLeader()) {
                // I'm not the leader. 
                //TODO: return who's the leader
                return null;
            }

            ProcessRequest runProcessRequestProtocol = this.RunProcessRequestProtocol(readRequest);
            if (runProcessRequestProtocol == ProcessRequest.DROP) {
                return null;
            }

            if (runProcessRequestProtocol == ProcessRequest.LAST_EXECUTION) {
                return this.replicaState.ClientTable[readRequest.ClientId].Item2;
            }

            return null;
        }

        public IResponse VisitPrepareMessage(PrepareMessage prepareMessage) {
            if (this.replicaState.OpNumber >= prepareMessage.OpNumber) {
                return new PrepareOk(this.replicaState.ServerId, this.replicaState.ViewNumber, prepareMessage.OpNumber);
            }
            // It must wait for previous messages.
            while (this.replicaState.OpNumber != (prepareMessage.OpNumber - 1)) {
                this.replicaState.HandlersPrepare.WaitOne();
            }

            int opNumber;
            int replicaView;
            lock (this.replicaState) {
                replicaView = this.replicaState.ViewNumber;
                opNumber = this.replicaState.IncrementOpNumberNumber();
                this.replicaState.Logger.Add(prepareMessage.ClientRequest);
            }

            // Notify all threads that are waiting for new prepare messages
            this.replicaState.HandlersPrepare.Set();
            this.replicaState.HandlersPrepare.Reset();
            this.replicaState.HandlersCommits.Set();
            this.replicaState.HandlersCommits.Reset();

            return new PrepareOk(this.replicaState.ServerId, replicaView, opNumber);
        }

        public IResponse VisitCommitMessage(CommitMessage commitMessage) {
            if (commitMessage.CommitNumber < this.replicaState.CommitNumber &&
                commitMessage.ViewNumber != this.replicaState.ViewNumber &&
                commitMessage.ServerId != this.replicaState.Leader) {
                return null;
            }
            // It must confirm that it received the prepare message.
            while (commitMessage.CommitNumber > this.replicaState.OpNumber) {
                this.replicaState.HandlersCommits.WaitOne();
            }
            this.replicaState.ExecuteFromUntil(this.replicaState.CommitNumber, commitMessage.CommitNumber - 1);
            ClientRequest request = this.replicaState.Logger[commitMessage.CommitNumber - 1];
            Log.Debug($"Requesting {request} to Tuple Space.");

            Executor requestExecutor = ExecutorFactory.Factory(request, commitMessage.CommitNumber);

            // Add Request to Queue to be executed
            OrderedQueue.AddRequestToQueue(this.replicaState, request, requestExecutor);
            return null;
        }

        public IResponse VisitStartViewChange(StartViewChange startViewChange) {
            if (this.replicaState.HandlerStateChanged.WaitOne((int)(Timeout.TIMEOUT_HEART_BEAT * 1.2)) &&
                this.replicaState.State is ViewChangeMessageProcessor) {
                return startViewChange.Accept(this.replicaState.State);
            }
            return null;
        }

        public IResponse VisitDoViewChange(DoViewChange doViewChange) {
            lock (this.replicaState.State) {
                if (!(this.replicaState.State is ViewChangeMessageProcessor)) {
                    this.replicaState.ChangeToViewChange(doViewChange.ViewNumber, doViewChange.Configuration);
                }
            }
            return doViewChange.Accept(this.replicaState.State);
        }

        public IResponse VisitStartChange(StartChange startChange) {
            lock (this.replicaState.State) {
                if (!(this.replicaState.State is ViewChangeMessageProcessor)) {
                    this.replicaState.ChangeToViewChange(startChange);
                }
            }
            return startChange.Accept(this.replicaState.State);
        }

        public IResponse VisitRecovery(Recovery recovery) {
            if (this.replicaState.OpNumber < recovery.OpNumber) {
                return new RecoveryResponse(this.replicaState.ServerId);
            }

            int count = this.replicaState.Logger.Count;

            return new RecoveryResponse(
                this.replicaState.ServerId,
                this.replicaState.ViewNumber,
                this.replicaState.OpNumber,
                this.replicaState.CommitNumber,
                this.replicaState.Logger.GetRange(recovery.OpNumber, count - recovery.OpNumber));
        }

        public IResponse VisitClientHandShakeRequest(ClientHandShakeRequest clientHandShakeRequest) {
            Uri[] viewConfiguration = this.replicaState.Configuration.Values.ToArray();
            return new ClientHandShakeResponse(
                Protocol.StateMachineReplication, 
                this.replicaState.ViewNumber, 
                viewConfiguration,
                this.replicaState.Configuration[this.replicaState.Leader]);
        }

        public IResponse VisitServerHandShakeRequest(ServerHandShakeRequest serverHandShakeRequest) {
            return new ServerHandShakeResponse(this.replicaState.Configuration.Values.ToArray());
        }

        public IResponse VisitJoinView(JoinView joinView) {
            Log.Info($"JoinView issued for server {joinView.ServerId}");
            int newViewNumber = this.replicaState.ViewNumber + 1;
            SortedDictionary<string, Uri> newConfiguration = new SortedDictionary<string, Uri>(this.replicaState.Configuration);
            newConfiguration.Add(joinView.ServerId, joinView.Url);

            this.replicaState.ChangeToViewChange(newViewNumber, newConfiguration);
            return null;
        }

        private ProcessRequest RunProcessRequestProtocol(ClientRequest clientRequest) {
            if (this.replicaState.ClientTable.TryGetValue(clientRequest.ClientId, out Tuple<int, ClientResponse> clientResponse)) {
                // Key is in the dictionary
                if (clientResponse == null || clientResponse.Item1 < 0 ||
                    clientRequest.RequestNumber < clientResponse.Item1) {
                    // Duplicate Request: Long forgotten => drop
                    return ProcessRequest.DROP;
                }

                // Execute the requests in client's casual order
                if (clientRequest.RequestNumber != clientResponse.Item1 + 1) {
                    if (!this.replicaState.HandlersClient.ContainsKey(clientRequest.ClientId)) {
                        this.replicaState.HandlersClient.TryAdd(
                            clientRequest.ClientId,
                            new EventWaitHandle(false, EventResetMode.ManualReset));
                    }

                    this.replicaState.HandlersClient.TryGetValue(
                        clientRequest.ClientId,
                        out EventWaitHandle myHandler);
                    while (clientRequest.RequestNumber != clientResponse.Item1 + 1) {
                        myHandler.WaitOne();
                    }
                }

                if (clientRequest.RequestNumber == clientResponse.Item1) {
                    // Duplicate Request
                    // If it is in execution.. wait.
                    if (clientResponse.Item2.GetType() == typeof(Executor)) {
                        Executor executor = (Executor)clientResponse.Item2;
                        executor.Executed.WaitOne();
                    }
                    return ProcessRequest.LAST_EXECUTION;
                }
            } else {
                // Not in dictionary... Add with value as null
                this.replicaState.ClientTable.Add(clientRequest.ClientId, new Tuple<int, ClientResponse>(-1, null));
            }

            // Send Prepare Message and waits for f replies. opNumber is the order we agreed upon.
            int opNumber = this.SendPrepareMessage(clientRequest);
            Executor clientExecutor = ExecutorFactory.Factory(clientRequest, opNumber);

            // Add request to queue
            OrderedQueue.AddRequestToQueue(this.replicaState, clientRequest, clientExecutor);

            // Wait execution
            clientExecutor.Executed.WaitOne();

            return ProcessRequest.LAST_EXECUTION;
        }

        private int SendPrepareMessage(ClientRequest clientRequest) {
            int viewNumber;
            int opNumber;
            int commitNumber;
            Uri[] replicasUrls;

            lock (this.replicaState) {
                viewNumber = this.replicaState.ViewNumber;
                commitNumber = this.replicaState.CommitNumber;
                opNumber = this.replicaState.IncrementOpNumberNumber();
                this.replicaState.Logger.Add(clientRequest);
                replicasUrls = this.replicaState.ReplicasUrl.ToArray();
            }

            PrepareMessage prepareMessage = new PrepareMessage(
                this.replicaState.ServerId,
                viewNumber,
                clientRequest,
                opNumber,
                commitNumber);

            // Wait for (Number backup replicas + the leader) / 2
            int f = this.replicaState.Configuration.Count / 2;
            this.messageServiceClient.RequestMulticast(prepareMessage, replicasUrls, f, -1, true);

            return opNumber;
        }

        public override string ToString() {
            return "Normal";
        }
    }
}