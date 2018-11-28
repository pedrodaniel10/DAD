﻿using System.Threading;

namespace MessageService.Serializable {
    public interface IExecutorVisitor {
        void ExecuteAdd(AddExecutor addExecutor);

        void ExecuteTake(TakeExecutor takeExecutor);

        void ExecuteRead(ReadExecutor readExecutor);
    }

    public abstract class Executor : ClientResponse {
        public string ClientId { get; set; }
        public string Tuple { get; set; }
        public new int RequestNumber { get; set; }
        public int OpNumber { get; set; }
        public AutoResetEvent Executed { get; set; }

        protected Executor(string clientId, int requestNumber, string tuple) {
            this.ClientId = clientId;
            this.Tuple = tuple;
            this.RequestNumber = requestNumber;
            this.Executed = new AutoResetEvent(false);
        }

        protected Executor(ClientRequest clientRequest) {
            this.ClientId = clientRequest.ClientId;
            this.Tuple = clientRequest.Tuple;
            this.RequestNumber = clientRequest.RequestNumber;
            this.Executed = new AutoResetEvent(false);
        }

        public abstract void Execute(IExecutorVisitor visitor);
    }

    public class AddExecutor : Executor {
        public AddExecutor(string clientId, int requestNumber, string tuple) 
            : base(clientId, requestNumber, tuple) {}

        public AddExecutor(ClientRequest clientRequest) : base(clientRequest) { }

        public override void Execute(IExecutorVisitor visitor) {
            visitor.ExecuteAdd(this);
        }
    }

    public class TakeExecutor : Executor {
        public TakeExecutor(string clientId, int requestNumber, string tuple)
            : base(clientId, requestNumber, tuple) { }

        public TakeExecutor(ClientRequest clientRequest) : base(clientRequest) { }

        public override void Execute(IExecutorVisitor visitor) {
            visitor.ExecuteTake(this);
        }
    }

    public class ReadExecutor : Executor {
        public ReadExecutor(string clientId, int requestNumber, string tuple)
            : base(clientId, requestNumber, tuple) { }

        public ReadExecutor(ClientRequest clientRequest) : base(clientRequest) { }

        public override void Execute(IExecutorVisitor visitor) {
            visitor.ExecuteRead(this);
        }
    }
}