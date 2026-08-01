using Capnp;
using Capnp.Rpc;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CapnpGen
{
    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xf48b621dd5cd54e3UL), Proxy(typeof(GatewayDispatch_Proxy)), Skeleton(typeof(GatewayDispatch_Skeleton))]
    public interface IGatewayDispatch : IDisposable
    {
        Task<CapnpGen.DispatchResponse> Dispatch(CapnpGen.DispatchRequest request, CancellationToken cancellationToken_ = default);
        Task ReportUsage(CapnpGen.UsageReport report, CancellationToken cancellationToken_ = default);
        Task Abort(string leaseToken, string reason, CancellationToken cancellationToken_ = default);
        Task ReportUpstreamError(CapnpGen.ErrorReport report, CancellationToken cancellationToken_ = default);
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xf48b621dd5cd54e3UL)]
    public class GatewayDispatch_Proxy : Proxy, IGatewayDispatch
    {
        public async Task<CapnpGen.DispatchResponse> Dispatch(CapnpGen.DispatchRequest request, CancellationToken cancellationToken_ = default)
        {
            var in_ = SerializerState.CreateForRpc<CapnpGen.GatewayDispatch.Params_Dispatch.WRITER>();
            var arg_ = new CapnpGen.GatewayDispatch.Params_Dispatch()
            {Request = request};
            arg_?.serialize(in_);
            using (var d_ = await Call(17621285847297774819UL, 0, in_.Rewrap<DynamicSerializerState>(), false, cancellationToken_).WhenReturned)
            {
                var r_ = CapnpSerializable.Create<CapnpGen.GatewayDispatch.Result_Dispatch>(d_);
                return (r_.Response);
            }
        }

        public async Task ReportUsage(CapnpGen.UsageReport report, CancellationToken cancellationToken_ = default)
        {
            var in_ = SerializerState.CreateForRpc<CapnpGen.GatewayDispatch.Params_ReportUsage.WRITER>();
            var arg_ = new CapnpGen.GatewayDispatch.Params_ReportUsage()
            {Report = report};
            arg_?.serialize(in_);
            using (var d_ = await Call(17621285847297774819UL, 1, in_.Rewrap<DynamicSerializerState>(), false, cancellationToken_).WhenReturned)
            {
                var r_ = CapnpSerializable.Create<CapnpGen.GatewayDispatch.Result_ReportUsage>(d_);
                return;
            }
        }

        public async Task Abort(string leaseToken, string reason, CancellationToken cancellationToken_ = default)
        {
            var in_ = SerializerState.CreateForRpc<CapnpGen.GatewayDispatch.Params_Abort.WRITER>();
            var arg_ = new CapnpGen.GatewayDispatch.Params_Abort()
            {LeaseToken = leaseToken, Reason = reason};
            arg_?.serialize(in_);
            using (var d_ = await Call(17621285847297774819UL, 2, in_.Rewrap<DynamicSerializerState>(), false, cancellationToken_).WhenReturned)
            {
                var r_ = CapnpSerializable.Create<CapnpGen.GatewayDispatch.Result_Abort>(d_);
                return;
            }
        }

        public async Task ReportUpstreamError(CapnpGen.ErrorReport report, CancellationToken cancellationToken_ = default)
        {
            var in_ = SerializerState.CreateForRpc<CapnpGen.GatewayDispatch.Params_ReportUpstreamError.WRITER>();
            var arg_ = new CapnpGen.GatewayDispatch.Params_ReportUpstreamError()
            {Report = report};
            arg_?.serialize(in_);
            using (var d_ = await Call(17621285847297774819UL, 3, in_.Rewrap<DynamicSerializerState>(), false, cancellationToken_).WhenReturned)
            {
                var r_ = CapnpSerializable.Create<CapnpGen.GatewayDispatch.Result_ReportUpstreamError>(d_);
                return;
            }
        }
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xf48b621dd5cd54e3UL)]
    public class GatewayDispatch_Skeleton : Skeleton<IGatewayDispatch>
    {
        public GatewayDispatch_Skeleton()
        {
            SetMethodTable(Dispatch, ReportUsage, Abort, ReportUpstreamError);
        }

        public override ulong InterfaceId => 17621285847297774819UL;
        Task<AnswerOrCounterquestion> Dispatch(DeserializerState d_, CancellationToken cancellationToken_)
        {
            using (d_)
            {
                var in_ = CapnpSerializable.Create<CapnpGen.GatewayDispatch.Params_Dispatch>(d_);
                return Impatient.MaybeTailCall(Impl.Dispatch(in_.Request, cancellationToken_), response =>
                {
                    var s_ = SerializerState.CreateForRpc<CapnpGen.GatewayDispatch.Result_Dispatch.WRITER>();
                    var r_ = new CapnpGen.GatewayDispatch.Result_Dispatch{Response = response};
                    r_.serialize(s_);
                    return s_;
                }

                );
            }
        }

        async Task<AnswerOrCounterquestion> ReportUsage(DeserializerState d_, CancellationToken cancellationToken_)
        {
            using (d_)
            {
                var in_ = CapnpSerializable.Create<CapnpGen.GatewayDispatch.Params_ReportUsage>(d_);
                await Impl.ReportUsage(in_.Report, cancellationToken_);
                var s_ = SerializerState.CreateForRpc<CapnpGen.GatewayDispatch.Result_ReportUsage.WRITER>();
                return s_;
            }
        }

        async Task<AnswerOrCounterquestion> Abort(DeserializerState d_, CancellationToken cancellationToken_)
        {
            using (d_)
            {
                var in_ = CapnpSerializable.Create<CapnpGen.GatewayDispatch.Params_Abort>(d_);
                await Impl.Abort(in_.LeaseToken, in_.Reason, cancellationToken_);
                var s_ = SerializerState.CreateForRpc<CapnpGen.GatewayDispatch.Result_Abort.WRITER>();
                return s_;
            }
        }

        async Task<AnswerOrCounterquestion> ReportUpstreamError(DeserializerState d_, CancellationToken cancellationToken_)
        {
            using (d_)
            {
                var in_ = CapnpSerializable.Create<CapnpGen.GatewayDispatch.Params_ReportUpstreamError>(d_);
                await Impl.ReportUpstreamError(in_.Report, cancellationToken_);
                var s_ = SerializerState.CreateForRpc<CapnpGen.GatewayDispatch.Result_ReportUpstreamError.WRITER>();
                return s_;
            }
        }
    }

    public static class GatewayDispatch
    {
        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xe162e1ad1b685781UL)]
        public class Params_Dispatch : ICapnpSerializable
        {
            public const UInt64 typeId = 0xe162e1ad1b685781UL;
            void ICapnpSerializable.Deserialize(DeserializerState arg_)
            {
                var reader = READER.create(arg_);
                Request = CapnpSerializable.Create<CapnpGen.DispatchRequest>(reader.Request);
                applyDefaults();
            }

            public void serialize(WRITER writer)
            {
                Request?.serialize(writer.Request);
            }

            void ICapnpSerializable.Serialize(SerializerState arg_)
            {
                serialize(arg_.Rewrap<WRITER>());
            }

            public void applyDefaults()
            {
            }

            public CapnpGen.DispatchRequest Request
            {
                get;
                set;
            }

            public struct READER
            {
                readonly DeserializerState ctx;
                public READER(DeserializerState ctx)
                {
                    this.ctx = ctx;
                }

                public static READER create(DeserializerState ctx) => new READER(ctx);
                public static implicit operator DeserializerState(READER reader) => reader.ctx;
                public static implicit operator READER(DeserializerState ctx) => new READER(ctx);
                public CapnpGen.DispatchRequest.READER Request => ctx.ReadStruct(0, CapnpGen.DispatchRequest.READER.create);
            }

            public class WRITER : SerializerState
            {
                public WRITER()
                {
                    this.SetStruct(0, 1);
                }

                public CapnpGen.DispatchRequest.WRITER Request
                {
                    get => BuildPointer<CapnpGen.DispatchRequest.WRITER>(0);
                    set => Link(0, value);
                }
            }
        }

        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xf8c65b2c74592831UL)]
        public class Result_Dispatch : ICapnpSerializable
        {
            public const UInt64 typeId = 0xf8c65b2c74592831UL;
            void ICapnpSerializable.Deserialize(DeserializerState arg_)
            {
                var reader = READER.create(arg_);
                Response = CapnpSerializable.Create<CapnpGen.DispatchResponse>(reader.Response);
                applyDefaults();
            }

            public void serialize(WRITER writer)
            {
                Response?.serialize(writer.Response);
            }

            void ICapnpSerializable.Serialize(SerializerState arg_)
            {
                serialize(arg_.Rewrap<WRITER>());
            }

            public void applyDefaults()
            {
            }

            public CapnpGen.DispatchResponse Response
            {
                get;
                set;
            }

            public struct READER
            {
                readonly DeserializerState ctx;
                public READER(DeserializerState ctx)
                {
                    this.ctx = ctx;
                }

                public static READER create(DeserializerState ctx) => new READER(ctx);
                public static implicit operator DeserializerState(READER reader) => reader.ctx;
                public static implicit operator READER(DeserializerState ctx) => new READER(ctx);
                public CapnpGen.DispatchResponse.READER Response => ctx.ReadStruct(0, CapnpGen.DispatchResponse.READER.create);
            }

            public class WRITER : SerializerState
            {
                public WRITER()
                {
                    this.SetStruct(0, 1);
                }

                public CapnpGen.DispatchResponse.WRITER Response
                {
                    get => BuildPointer<CapnpGen.DispatchResponse.WRITER>(0);
                    set => Link(0, value);
                }
            }
        }

        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0x85f09f825414b69eUL)]
        public class Params_ReportUsage : ICapnpSerializable
        {
            public const UInt64 typeId = 0x85f09f825414b69eUL;
            void ICapnpSerializable.Deserialize(DeserializerState arg_)
            {
                var reader = READER.create(arg_);
                Report = CapnpSerializable.Create<CapnpGen.UsageReport>(reader.Report);
                applyDefaults();
            }

            public void serialize(WRITER writer)
            {
                Report?.serialize(writer.Report);
            }

            void ICapnpSerializable.Serialize(SerializerState arg_)
            {
                serialize(arg_.Rewrap<WRITER>());
            }

            public void applyDefaults()
            {
            }

            public CapnpGen.UsageReport Report
            {
                get;
                set;
            }

            public struct READER
            {
                readonly DeserializerState ctx;
                public READER(DeserializerState ctx)
                {
                    this.ctx = ctx;
                }

                public static READER create(DeserializerState ctx) => new READER(ctx);
                public static implicit operator DeserializerState(READER reader) => reader.ctx;
                public static implicit operator READER(DeserializerState ctx) => new READER(ctx);
                public CapnpGen.UsageReport.READER Report => ctx.ReadStruct(0, CapnpGen.UsageReport.READER.create);
            }

            public class WRITER : SerializerState
            {
                public WRITER()
                {
                    this.SetStruct(0, 1);
                }

                public CapnpGen.UsageReport.WRITER Report
                {
                    get => BuildPointer<CapnpGen.UsageReport.WRITER>(0);
                    set => Link(0, value);
                }
            }
        }

        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xcdb033115ecceee8UL)]
        public class Result_ReportUsage : ICapnpSerializable
        {
            public const UInt64 typeId = 0xcdb033115ecceee8UL;
            void ICapnpSerializable.Deserialize(DeserializerState arg_)
            {
                var reader = READER.create(arg_);
                applyDefaults();
            }

            public void serialize(WRITER writer)
            {
            }

            void ICapnpSerializable.Serialize(SerializerState arg_)
            {
                serialize(arg_.Rewrap<WRITER>());
            }

            public void applyDefaults()
            {
            }

            public struct READER
            {
                readonly DeserializerState ctx;
                public READER(DeserializerState ctx)
                {
                    this.ctx = ctx;
                }

                public static READER create(DeserializerState ctx) => new READER(ctx);
                public static implicit operator DeserializerState(READER reader) => reader.ctx;
                public static implicit operator READER(DeserializerState ctx) => new READER(ctx);
            }

            public class WRITER : SerializerState
            {
                public WRITER()
                {
                    this.SetStruct(0, 0);
                }
            }
        }

        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xe729e879f57f5caaUL)]
        public class Params_Abort : ICapnpSerializable
        {
            public const UInt64 typeId = 0xe729e879f57f5caaUL;
            void ICapnpSerializable.Deserialize(DeserializerState arg_)
            {
                var reader = READER.create(arg_);
                LeaseToken = reader.LeaseToken;
                Reason = reader.Reason;
                applyDefaults();
            }

            public void serialize(WRITER writer)
            {
                writer.LeaseToken = LeaseToken;
                writer.Reason = Reason;
            }

            void ICapnpSerializable.Serialize(SerializerState arg_)
            {
                serialize(arg_.Rewrap<WRITER>());
            }

            public void applyDefaults()
            {
            }

            public string LeaseToken
            {
                get;
                set;
            }

            public string Reason
            {
                get;
                set;
            }

            public struct READER
            {
                readonly DeserializerState ctx;
                public READER(DeserializerState ctx)
                {
                    this.ctx = ctx;
                }

                public static READER create(DeserializerState ctx) => new READER(ctx);
                public static implicit operator DeserializerState(READER reader) => reader.ctx;
                public static implicit operator READER(DeserializerState ctx) => new READER(ctx);
                public string LeaseToken => ctx.ReadText(0, null);
                public string Reason => ctx.ReadText(1, null);
            }

            public class WRITER : SerializerState
            {
                public WRITER()
                {
                    this.SetStruct(0, 2);
                }

                public string LeaseToken
                {
                    get => this.ReadText(0, null);
                    set => this.WriteText(0, value, null);
                }

                public string Reason
                {
                    get => this.ReadText(1, null);
                    set => this.WriteText(1, value, null);
                }
            }
        }

        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xb6d18a83bfc0e123UL)]
        public class Result_Abort : ICapnpSerializable
        {
            public const UInt64 typeId = 0xb6d18a83bfc0e123UL;
            void ICapnpSerializable.Deserialize(DeserializerState arg_)
            {
                var reader = READER.create(arg_);
                applyDefaults();
            }

            public void serialize(WRITER writer)
            {
            }

            void ICapnpSerializable.Serialize(SerializerState arg_)
            {
                serialize(arg_.Rewrap<WRITER>());
            }

            public void applyDefaults()
            {
            }

            public struct READER
            {
                readonly DeserializerState ctx;
                public READER(DeserializerState ctx)
                {
                    this.ctx = ctx;
                }

                public static READER create(DeserializerState ctx) => new READER(ctx);
                public static implicit operator DeserializerState(READER reader) => reader.ctx;
                public static implicit operator READER(DeserializerState ctx) => new READER(ctx);
            }

            public class WRITER : SerializerState
            {
                public WRITER()
                {
                    this.SetStruct(0, 0);
                }
            }
        }

        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xd8e81b33623bf4e2UL)]
        public class Params_ReportUpstreamError : ICapnpSerializable
        {
            public const UInt64 typeId = 0xd8e81b33623bf4e2UL;
            void ICapnpSerializable.Deserialize(DeserializerState arg_)
            {
                var reader = READER.create(arg_);
                Report = CapnpSerializable.Create<CapnpGen.ErrorReport>(reader.Report);
                applyDefaults();
            }

            public void serialize(WRITER writer)
            {
                Report?.serialize(writer.Report);
            }

            void ICapnpSerializable.Serialize(SerializerState arg_)
            {
                serialize(arg_.Rewrap<WRITER>());
            }

            public void applyDefaults()
            {
            }

            public CapnpGen.ErrorReport Report
            {
                get;
                set;
            }

            public struct READER
            {
                readonly DeserializerState ctx;
                public READER(DeserializerState ctx)
                {
                    this.ctx = ctx;
                }

                public static READER create(DeserializerState ctx) => new READER(ctx);
                public static implicit operator DeserializerState(READER reader) => reader.ctx;
                public static implicit operator READER(DeserializerState ctx) => new READER(ctx);
                public CapnpGen.ErrorReport.READER Report => ctx.ReadStruct(0, CapnpGen.ErrorReport.READER.create);
            }

            public class WRITER : SerializerState
            {
                public WRITER()
                {
                    this.SetStruct(0, 1);
                }

                public CapnpGen.ErrorReport.WRITER Report
                {
                    get => BuildPointer<CapnpGen.ErrorReport.WRITER>(0);
                    set => Link(0, value);
                }
            }
        }

        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xde0b7dfee36fb939UL)]
        public class Result_ReportUpstreamError : ICapnpSerializable
        {
            public const UInt64 typeId = 0xde0b7dfee36fb939UL;
            void ICapnpSerializable.Deserialize(DeserializerState arg_)
            {
                var reader = READER.create(arg_);
                applyDefaults();
            }

            public void serialize(WRITER writer)
            {
            }

            void ICapnpSerializable.Serialize(SerializerState arg_)
            {
                serialize(arg_.Rewrap<WRITER>());
            }

            public void applyDefaults()
            {
            }

            public struct READER
            {
                readonly DeserializerState ctx;
                public READER(DeserializerState ctx)
                {
                    this.ctx = ctx;
                }

                public static READER create(DeserializerState ctx) => new READER(ctx);
                public static implicit operator DeserializerState(READER reader) => reader.ctx;
                public static implicit operator READER(DeserializerState ctx) => new READER(ctx);
            }

            public class WRITER : SerializerState
            {
                public WRITER()
                {
                    this.SetStruct(0, 0);
                }
            }
        }
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0x9fa0c5002b3d792cUL)]
    public class DispatchRequest : ICapnpSerializable
    {
        public const UInt64 typeId = 0x9fa0c5002b3d792cUL;
        void ICapnpSerializable.Deserialize(DeserializerState arg_)
        {
            var reader = READER.create(arg_);
            ApiKeyHash = reader.ApiKeyHash;
            RequestedModel = reader.RequestedModel;
            SessionHash = reader.SessionHash;
            ClientIp = reader.ClientIp;
            RequestId = reader.RequestId;
            ExcludedAccounts = reader.ExcludedAccounts;
            CachedAuthVersion = reader.CachedAuthVersion;
            Endpoint = reader.Endpoint;
            MetadataUserId = reader.MetadataUserId;
            applyDefaults();
        }

        public void serialize(WRITER writer)
        {
            writer.ApiKeyHash = ApiKeyHash;
            writer.RequestedModel = RequestedModel;
            writer.SessionHash = SessionHash;
            writer.ClientIp = ClientIp;
            writer.RequestId = RequestId;
            writer.ExcludedAccounts.Init(ExcludedAccounts);
            writer.CachedAuthVersion = CachedAuthVersion;
            writer.Endpoint = Endpoint;
            writer.MetadataUserId = MetadataUserId;
        }

        void ICapnpSerializable.Serialize(SerializerState arg_)
        {
            serialize(arg_.Rewrap<WRITER>());
        }

        public void applyDefaults()
        {
        }

        public string ApiKeyHash
        {
            get;
            set;
        }

        public string RequestedModel
        {
            get;
            set;
        }

        public string SessionHash
        {
            get;
            set;
        }

        public string ClientIp
        {
            get;
            set;
        }

        public string RequestId
        {
            get;
            set;
        }

        public IReadOnlyList<long> ExcludedAccounts
        {
            get;
            set;
        }

        public long CachedAuthVersion
        {
            get;
            set;
        }

        public CapnpGen.DispatchRequest.EndpointKind Endpoint
        {
            get;
            set;
        }

        public string MetadataUserId
        {
            get;
            set;
        }

        public struct READER
        {
            readonly DeserializerState ctx;
            public READER(DeserializerState ctx)
            {
                this.ctx = ctx;
            }

            public static READER create(DeserializerState ctx) => new READER(ctx);
            public static implicit operator DeserializerState(READER reader) => reader.ctx;
            public static implicit operator READER(DeserializerState ctx) => new READER(ctx);
            public string ApiKeyHash => ctx.ReadText(0, null);
            public string RequestedModel => ctx.ReadText(1, null);
            public string SessionHash => ctx.ReadText(2, null);
            public string ClientIp => ctx.ReadText(3, null);
            public string RequestId => ctx.ReadText(4, null);
            public IReadOnlyList<long> ExcludedAccounts => ctx.ReadList(5).CastLong();
            public long CachedAuthVersion => ctx.ReadDataLong(0UL, 0L);
            public CapnpGen.DispatchRequest.EndpointKind Endpoint => (CapnpGen.DispatchRequest.EndpointKind)ctx.ReadDataUShort(64UL, (ushort)0);
            public string MetadataUserId => ctx.ReadText(6, null);
        }

        public class WRITER : SerializerState
        {
            public WRITER()
            {
                this.SetStruct(2, 7);
            }

            public string ApiKeyHash
            {
                get => this.ReadText(0, null);
                set => this.WriteText(0, value, null);
            }

            public string RequestedModel
            {
                get => this.ReadText(1, null);
                set => this.WriteText(1, value, null);
            }

            public string SessionHash
            {
                get => this.ReadText(2, null);
                set => this.WriteText(2, value, null);
            }

            public string ClientIp
            {
                get => this.ReadText(3, null);
                set => this.WriteText(3, value, null);
            }

            public string RequestId
            {
                get => this.ReadText(4, null);
                set => this.WriteText(4, value, null);
            }

            public ListOfPrimitivesSerializer<long> ExcludedAccounts
            {
                get => BuildPointer<ListOfPrimitivesSerializer<long>>(5);
                set => Link(5, value);
            }

            public long CachedAuthVersion
            {
                get => this.ReadDataLong(0UL, 0L);
                set => this.WriteData(0UL, value, 0L);
            }

            public CapnpGen.DispatchRequest.EndpointKind Endpoint
            {
                get => (CapnpGen.DispatchRequest.EndpointKind)this.ReadDataUShort(64UL, (ushort)0);
                set => this.WriteData(64UL, (ushort)value, (ushort)0);
            }

            public string MetadataUserId
            {
                get => this.ReadText(6, null);
                set => this.WriteText(6, value, null);
            }
        }

        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0x976eb119faf03477UL)]
        public enum EndpointKind : ushort
        {
            messages,
            chatCompletions,
            responses,
            embeddings,
            images,
            gemini
        }
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xbd8ad600ee7246daUL)]
    public class DispatchResponse : ICapnpSerializable
    {
        public const UInt64 typeId = 0xbd8ad600ee7246daUL;
        void ICapnpSerializable.Deserialize(DeserializerState arg_)
        {
            var reader = READER.create(arg_);
            TheOutcome = reader.TheOutcome;
            AuthVersion = reader.AuthVersion;
            Auth = CapnpSerializable.Create<CapnpGen.AuthSnapshot>(reader.Auth);
            Upstream = CapnpSerializable.Create<CapnpGen.UpstreamTarget>(reader.Upstream);
            WaitPlan = CapnpSerializable.Create<CapnpGen.WaitPlan>(reader.WaitPlan);
            Reject = CapnpSerializable.Create<CapnpGen.RejectInfo>(reader.Reject);
            LeaseToken = reader.LeaseToken;
            applyDefaults();
        }

        public void serialize(WRITER writer)
        {
            writer.TheOutcome = TheOutcome;
            writer.AuthVersion = AuthVersion;
            Auth?.serialize(writer.Auth);
            Upstream?.serialize(writer.Upstream);
            WaitPlan?.serialize(writer.WaitPlan);
            Reject?.serialize(writer.Reject);
            writer.LeaseToken = LeaseToken;
        }

        void ICapnpSerializable.Serialize(SerializerState arg_)
        {
            serialize(arg_.Rewrap<WRITER>());
        }

        public void applyDefaults()
        {
        }

        public CapnpGen.DispatchResponse.Outcome TheOutcome
        {
            get;
            set;
        }

        public long AuthVersion
        {
            get;
            set;
        }

        public CapnpGen.AuthSnapshot Auth
        {
            get;
            set;
        }

        public CapnpGen.UpstreamTarget Upstream
        {
            get;
            set;
        }

        public CapnpGen.WaitPlan WaitPlan
        {
            get;
            set;
        }

        public CapnpGen.RejectInfo Reject
        {
            get;
            set;
        }

        public string LeaseToken
        {
            get;
            set;
        }

        public struct READER
        {
            readonly DeserializerState ctx;
            public READER(DeserializerState ctx)
            {
                this.ctx = ctx;
            }

            public static READER create(DeserializerState ctx) => new READER(ctx);
            public static implicit operator DeserializerState(READER reader) => reader.ctx;
            public static implicit operator READER(DeserializerState ctx) => new READER(ctx);
            public CapnpGen.DispatchResponse.Outcome TheOutcome => (CapnpGen.DispatchResponse.Outcome)ctx.ReadDataUShort(0UL, (ushort)0);
            public long AuthVersion => ctx.ReadDataLong(64UL, 0L);
            public CapnpGen.AuthSnapshot.READER Auth => ctx.ReadStruct(0, CapnpGen.AuthSnapshot.READER.create);
            public CapnpGen.UpstreamTarget.READER Upstream => ctx.ReadStruct(1, CapnpGen.UpstreamTarget.READER.create);
            public CapnpGen.WaitPlan.READER WaitPlan => ctx.ReadStruct(2, CapnpGen.WaitPlan.READER.create);
            public CapnpGen.RejectInfo.READER Reject => ctx.ReadStruct(3, CapnpGen.RejectInfo.READER.create);
            public string LeaseToken => ctx.ReadText(4, null);
        }

        public class WRITER : SerializerState
        {
            public WRITER()
            {
                this.SetStruct(2, 5);
            }

            public CapnpGen.DispatchResponse.Outcome TheOutcome
            {
                get => (CapnpGen.DispatchResponse.Outcome)this.ReadDataUShort(0UL, (ushort)0);
                set => this.WriteData(0UL, (ushort)value, (ushort)0);
            }

            public long AuthVersion
            {
                get => this.ReadDataLong(64UL, 0L);
                set => this.WriteData(64UL, value, 0L);
            }

            public CapnpGen.AuthSnapshot.WRITER Auth
            {
                get => BuildPointer<CapnpGen.AuthSnapshot.WRITER>(0);
                set => Link(0, value);
            }

            public CapnpGen.UpstreamTarget.WRITER Upstream
            {
                get => BuildPointer<CapnpGen.UpstreamTarget.WRITER>(1);
                set => Link(1, value);
            }

            public CapnpGen.WaitPlan.WRITER WaitPlan
            {
                get => BuildPointer<CapnpGen.WaitPlan.WRITER>(2);
                set => Link(2, value);
            }

            public CapnpGen.RejectInfo.WRITER Reject
            {
                get => BuildPointer<CapnpGen.RejectInfo.WRITER>(3);
                set => Link(3, value);
            }

            public string LeaseToken
            {
                get => this.ReadText(4, null);
                set => this.WriteText(4, value, null);
            }
        }

        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0x933ab989cf0c5407UL)]
        public enum Outcome : ushort
        {
            ok,
            wait,
            rejected,
            reauth
        }
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0x9fb37d25b1d00c50UL)]
    public class WaitPlan : ICapnpSerializable
    {
        public const UInt64 typeId = 0x9fb37d25b1d00c50UL;
        void ICapnpSerializable.Deserialize(DeserializerState arg_)
        {
            var reader = READER.create(arg_);
            AccountId = reader.AccountId;
            MaxConcurrency = reader.MaxConcurrency;
            TimeoutMs = reader.TimeoutMs;
            MaxWaiting = reader.MaxWaiting;
            applyDefaults();
        }

        public void serialize(WRITER writer)
        {
            writer.AccountId = AccountId;
            writer.MaxConcurrency = MaxConcurrency;
            writer.TimeoutMs = TimeoutMs;
            writer.MaxWaiting = MaxWaiting;
        }

        void ICapnpSerializable.Serialize(SerializerState arg_)
        {
            serialize(arg_.Rewrap<WRITER>());
        }

        public void applyDefaults()
        {
        }

        public long AccountId
        {
            get;
            set;
        }

        public int MaxConcurrency
        {
            get;
            set;
        }

        public int TimeoutMs
        {
            get;
            set;
        }

        public int MaxWaiting
        {
            get;
            set;
        }

        public struct READER
        {
            readonly DeserializerState ctx;
            public READER(DeserializerState ctx)
            {
                this.ctx = ctx;
            }

            public static READER create(DeserializerState ctx) => new READER(ctx);
            public static implicit operator DeserializerState(READER reader) => reader.ctx;
            public static implicit operator READER(DeserializerState ctx) => new READER(ctx);
            public long AccountId => ctx.ReadDataLong(0UL, 0L);
            public int MaxConcurrency => ctx.ReadDataInt(64UL, 0);
            public int TimeoutMs => ctx.ReadDataInt(96UL, 0);
            public int MaxWaiting => ctx.ReadDataInt(128UL, 0);
        }

        public class WRITER : SerializerState
        {
            public WRITER()
            {
                this.SetStruct(3, 0);
            }

            public long AccountId
            {
                get => this.ReadDataLong(0UL, 0L);
                set => this.WriteData(0UL, value, 0L);
            }

            public int MaxConcurrency
            {
                get => this.ReadDataInt(64UL, 0);
                set => this.WriteData(64UL, value, 0);
            }

            public int TimeoutMs
            {
                get => this.ReadDataInt(96UL, 0);
                set => this.WriteData(96UL, value, 0);
            }

            public int MaxWaiting
            {
                get => this.ReadDataInt(128UL, 0);
                set => this.WriteData(128UL, value, 0);
            }
        }
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xda2c209dfcfda4c5UL)]
    public class RejectInfo : ICapnpSerializable
    {
        public const UInt64 typeId = 0xda2c209dfcfda4c5UL;
        void ICapnpSerializable.Deserialize(DeserializerState arg_)
        {
            var reader = READER.create(arg_);
            Code = reader.Code;
            Message = reader.Message;
            RetryAfterMs = reader.RetryAfterMs;
            applyDefaults();
        }

        public void serialize(WRITER writer)
        {
            writer.Code = Code;
            writer.Message = Message;
            writer.RetryAfterMs = RetryAfterMs;
        }

        void ICapnpSerializable.Serialize(SerializerState arg_)
        {
            serialize(arg_.Rewrap<WRITER>());
        }

        public void applyDefaults()
        {
        }

        public CapnpGen.RejectInfo.RejectCode Code
        {
            get;
            set;
        }

        public string Message
        {
            get;
            set;
        }

        public int RetryAfterMs
        {
            get;
            set;
        }

        public struct READER
        {
            readonly DeserializerState ctx;
            public READER(DeserializerState ctx)
            {
                this.ctx = ctx;
            }

            public static READER create(DeserializerState ctx) => new READER(ctx);
            public static implicit operator DeserializerState(READER reader) => reader.ctx;
            public static implicit operator READER(DeserializerState ctx) => new READER(ctx);
            public CapnpGen.RejectInfo.RejectCode Code => (CapnpGen.RejectInfo.RejectCode)ctx.ReadDataUShort(0UL, (ushort)0);
            public string Message => ctx.ReadText(0, null);
            public int RetryAfterMs => ctx.ReadDataInt(32UL, 0);
        }

        public class WRITER : SerializerState
        {
            public WRITER()
            {
                this.SetStruct(1, 1);
            }

            public CapnpGen.RejectInfo.RejectCode Code
            {
                get => (CapnpGen.RejectInfo.RejectCode)this.ReadDataUShort(0UL, (ushort)0);
                set => this.WriteData(0UL, (ushort)value, (ushort)0);
            }

            public string Message
            {
                get => this.ReadText(0, null);
                set => this.WriteText(0, value, null);
            }

            public int RetryAfterMs
            {
                get => this.ReadDataInt(32UL, 0);
                set => this.WriteData(32UL, value, 0);
            }
        }

        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0x89307a3d798bd09dUL)]
        public enum RejectCode : ushort
        {
            invalidKey,
            expired,
            noBalance,
            rateLimited,
            noAccount,
            concurrencyExceeded,
            ipBlocked,
            quotaExhausted
        }
    }
}