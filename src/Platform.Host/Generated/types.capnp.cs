using Capnp;
using Capnp.Rpc;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CapnpGen
{
    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xc667b47365c1ff41UL)]
    public class AuthSnapshot : ICapnpSerializable
    {
        public const UInt64 typeId = 0xc667b47365c1ff41UL;
        void ICapnpSerializable.Deserialize(DeserializerState arg_)
        {
            var reader = READER.create(arg_);
            ApiKeyId = reader.ApiKeyId;
            UserId = reader.UserId;
            GroupId = reader.GroupId;
            Name = reader.Name;
            Status = reader.Status;
            IpWhitelist = reader.IpWhitelist;
            IpBlacklist = reader.IpBlacklist;
            User = CapnpSerializable.Create<CapnpGen.UserSnapshot>(reader.User);
            Group = CapnpSerializable.Create<CapnpGen.GroupSnapshot>(reader.Group);
            Quota = reader.Quota;
            QuotaUsed = reader.QuotaUsed;
            ExpiresAt = reader.ExpiresAt;
            RateLimit5h = reader.RateLimit5h;
            RateLimit1d = reader.RateLimit1d;
            RateLimit7d = reader.RateLimit7d;
            Version = reader.Version;
            applyDefaults();
        }

        public void serialize(WRITER writer)
        {
            writer.ApiKeyId = ApiKeyId;
            writer.UserId = UserId;
            writer.GroupId = GroupId;
            writer.Name = Name;
            writer.Status = Status;
            writer.IpWhitelist.Init(IpWhitelist);
            writer.IpBlacklist.Init(IpBlacklist);
            User?.serialize(writer.User);
            Group?.serialize(writer.Group);
            writer.Quota = Quota;
            writer.QuotaUsed = QuotaUsed;
            writer.ExpiresAt = ExpiresAt;
            writer.RateLimit5h = RateLimit5h;
            writer.RateLimit1d = RateLimit1d;
            writer.RateLimit7d = RateLimit7d;
            writer.Version = Version;
        }

        void ICapnpSerializable.Serialize(SerializerState arg_)
        {
            serialize(arg_.Rewrap<WRITER>());
        }

        public void applyDefaults()
        {
        }

        public long ApiKeyId
        {
            get;
            set;
        }

        public long UserId
        {
            get;
            set;
        }

        public long GroupId
        {
            get;
            set;
        }

        public string Name
        {
            get;
            set;
        }

        public string Status
        {
            get;
            set;
        }

        public IReadOnlyList<string> IpWhitelist
        {
            get;
            set;
        }

        public IReadOnlyList<string> IpBlacklist
        {
            get;
            set;
        }

        public CapnpGen.UserSnapshot User
        {
            get;
            set;
        }

        public CapnpGen.GroupSnapshot Group
        {
            get;
            set;
        }

        public long Quota
        {
            get;
            set;
        }

        public long QuotaUsed
        {
            get;
            set;
        }

        public long ExpiresAt
        {
            get;
            set;
        }

        public long RateLimit5h
        {
            get;
            set;
        }

        public long RateLimit1d
        {
            get;
            set;
        }

        public long RateLimit7d
        {
            get;
            set;
        }

        public long Version
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
            public long ApiKeyId => ctx.ReadDataLong(0UL, 0L);
            public long UserId => ctx.ReadDataLong(64UL, 0L);
            public long GroupId => ctx.ReadDataLong(128UL, 0L);
            public string Name => ctx.ReadText(0, null);
            public string Status => ctx.ReadText(1, null);
            public IReadOnlyList<string> IpWhitelist => ctx.ReadList(2).CastText2();
            public IReadOnlyList<string> IpBlacklist => ctx.ReadList(3).CastText2();
            public CapnpGen.UserSnapshot.READER User => ctx.ReadStruct(4, CapnpGen.UserSnapshot.READER.create);
            public CapnpGen.GroupSnapshot.READER Group => ctx.ReadStruct(5, CapnpGen.GroupSnapshot.READER.create);
            public long Quota => ctx.ReadDataLong(192UL, 0L);
            public long QuotaUsed => ctx.ReadDataLong(256UL, 0L);
            public long ExpiresAt => ctx.ReadDataLong(320UL, 0L);
            public long RateLimit5h => ctx.ReadDataLong(384UL, 0L);
            public long RateLimit1d => ctx.ReadDataLong(448UL, 0L);
            public long RateLimit7d => ctx.ReadDataLong(512UL, 0L);
            public long Version => ctx.ReadDataLong(576UL, 0L);
        }

        public class WRITER : SerializerState
        {
            public WRITER()
            {
                this.SetStruct(10, 6);
            }

            public long ApiKeyId
            {
                get => this.ReadDataLong(0UL, 0L);
                set => this.WriteData(0UL, value, 0L);
            }

            public long UserId
            {
                get => this.ReadDataLong(64UL, 0L);
                set => this.WriteData(64UL, value, 0L);
            }

            public long GroupId
            {
                get => this.ReadDataLong(128UL, 0L);
                set => this.WriteData(128UL, value, 0L);
            }

            public string Name
            {
                get => this.ReadText(0, null);
                set => this.WriteText(0, value, null);
            }

            public string Status
            {
                get => this.ReadText(1, null);
                set => this.WriteText(1, value, null);
            }

            public ListOfTextSerializer IpWhitelist
            {
                get => BuildPointer<ListOfTextSerializer>(2);
                set => Link(2, value);
            }

            public ListOfTextSerializer IpBlacklist
            {
                get => BuildPointer<ListOfTextSerializer>(3);
                set => Link(3, value);
            }

            public CapnpGen.UserSnapshot.WRITER User
            {
                get => BuildPointer<CapnpGen.UserSnapshot.WRITER>(4);
                set => Link(4, value);
            }

            public CapnpGen.GroupSnapshot.WRITER Group
            {
                get => BuildPointer<CapnpGen.GroupSnapshot.WRITER>(5);
                set => Link(5, value);
            }

            public long Quota
            {
                get => this.ReadDataLong(192UL, 0L);
                set => this.WriteData(192UL, value, 0);
            }

            public long QuotaUsed
            {
                get => this.ReadDataLong(256UL, 0L);
                set => this.WriteData(256UL, value, 0);
            }

            public long ExpiresAt
            {
                get => this.ReadDataLong(320UL, 0L);
                set => this.WriteData(320UL, value, 0L);
            }

            public long RateLimit5h
            {
                get => this.ReadDataLong(384UL, 0L);
                set => this.WriteData(384UL, value, 0);
            }

            public long RateLimit1d
            {
                get => this.ReadDataLong(448UL, 0L);
                set => this.WriteData(448UL, value, 0);
            }

            public long RateLimit7d
            {
                get => this.ReadDataLong(512UL, 0L);
                set => this.WriteData(512UL, value, 0);
            }

            public long Version
            {
                get => this.ReadDataLong(576UL, 0L);
                set => this.WriteData(576UL, value, 0L);
            }
        }
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xcee9fbb54af12aa4UL)]
    public class UserSnapshot : ICapnpSerializable
    {
        public const UInt64 typeId = 0xcee9fbb54af12aa4UL;
        void ICapnpSerializable.Deserialize(DeserializerState arg_)
        {
            var reader = READER.create(arg_);
            Id = reader.Id;
            Status = reader.Status;
            Role = reader.Role;
            Balance = reader.Balance;
            Concurrency = reader.Concurrency;
            AllowedGroups = reader.AllowedGroups;
            RpmLimit = reader.RpmLimit;
            applyDefaults();
        }

        public void serialize(WRITER writer)
        {
            writer.Id = Id;
            writer.Status = Status;
            writer.Role = Role;
            writer.Balance = Balance;
            writer.Concurrency = Concurrency;
            writer.AllowedGroups.Init(AllowedGroups);
            writer.RpmLimit = RpmLimit;
        }

        void ICapnpSerializable.Serialize(SerializerState arg_)
        {
            serialize(arg_.Rewrap<WRITER>());
        }

        public void applyDefaults()
        {
        }

        public long Id
        {
            get;
            set;
        }

        public string Status
        {
            get;
            set;
        }

        public string Role
        {
            get;
            set;
        }

        public long Balance
        {
            get;
            set;
        }

        public int Concurrency
        {
            get;
            set;
        }

        public IReadOnlyList<long> AllowedGroups
        {
            get;
            set;
        }

        public int RpmLimit
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
            public long Id => ctx.ReadDataLong(0UL, 0L);
            public string Status => ctx.ReadText(0, null);
            public string Role => ctx.ReadText(1, null);
            public long Balance => ctx.ReadDataLong(64UL, 0L);
            public int Concurrency => ctx.ReadDataInt(128UL, 0);
            public IReadOnlyList<long> AllowedGroups => ctx.ReadList(2).CastLong();
            public int RpmLimit => ctx.ReadDataInt(160UL, 0);
        }

        public class WRITER : SerializerState
        {
            public WRITER()
            {
                this.SetStruct(3, 3);
            }

            public long Id
            {
                get => this.ReadDataLong(0UL, 0L);
                set => this.WriteData(0UL, value, 0L);
            }

            public string Status
            {
                get => this.ReadText(0, null);
                set => this.WriteText(0, value, null);
            }

            public string Role
            {
                get => this.ReadText(1, null);
                set => this.WriteText(1, value, null);
            }

            public long Balance
            {
                get => this.ReadDataLong(64UL, 0L);
                set => this.WriteData(64UL, value, 0);
            }

            public int Concurrency
            {
                get => this.ReadDataInt(128UL, 0);
                set => this.WriteData(128UL, value, 0);
            }

            public ListOfPrimitivesSerializer<long> AllowedGroups
            {
                get => BuildPointer<ListOfPrimitivesSerializer<long>>(2);
                set => Link(2, value);
            }

            public int RpmLimit
            {
                get => this.ReadDataInt(160UL, 0);
                set => this.WriteData(160UL, value, 0);
            }
        }
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xbb7b3bb697705814UL)]
    public class GroupSnapshot : ICapnpSerializable
    {
        public const UInt64 typeId = 0xbb7b3bb697705814UL;
        void ICapnpSerializable.Deserialize(DeserializerState arg_)
        {
            var reader = READER.create(arg_);
            Id = reader.Id;
            Platform = reader.Platform;
            IsExclusive = reader.IsExclusive;
            Status = reader.Status;
            RateMultiplier = reader.RateMultiplier;
            DailyLimitUsd = reader.DailyLimitUsd;
            ClaudeCodeOnly = reader.ClaudeCodeOnly;
            FallbackGroupId = reader.FallbackGroupId;
            ModelRoutingEnabled = reader.ModelRoutingEnabled;
            applyDefaults();
        }

        public void serialize(WRITER writer)
        {
            writer.Id = Id;
            writer.Platform = Platform;
            writer.IsExclusive = IsExclusive;
            writer.Status = Status;
            writer.RateMultiplier = RateMultiplier;
            writer.DailyLimitUsd = DailyLimitUsd;
            writer.ClaudeCodeOnly = ClaudeCodeOnly;
            writer.FallbackGroupId = FallbackGroupId;
            writer.ModelRoutingEnabled = ModelRoutingEnabled;
        }

        void ICapnpSerializable.Serialize(SerializerState arg_)
        {
            serialize(arg_.Rewrap<WRITER>());
        }

        public void applyDefaults()
        {
        }

        public long Id
        {
            get;
            set;
        }

        public string Platform
        {
            get;
            set;
        }

        public bool IsExclusive
        {
            get;
            set;
        }

        public string Status
        {
            get;
            set;
        }

        public long RateMultiplier
        {
            get;
            set;
        }

        public long DailyLimitUsd
        {
            get;
            set;
        }

        public bool ClaudeCodeOnly
        {
            get;
            set;
        }

        public long FallbackGroupId
        {
            get;
            set;
        }

        public bool ModelRoutingEnabled
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
            public long Id => ctx.ReadDataLong(0UL, 0L);
            public string Platform => ctx.ReadText(0, null);
            public bool IsExclusive => ctx.ReadDataBool(64UL, false);
            public string Status => ctx.ReadText(1, null);
            public long RateMultiplier => ctx.ReadDataLong(128UL, 0L);
            public long DailyLimitUsd => ctx.ReadDataLong(192UL, 0L);
            public bool ClaudeCodeOnly => ctx.ReadDataBool(65UL, false);
            public long FallbackGroupId => ctx.ReadDataLong(256UL, 0L);
            public bool ModelRoutingEnabled => ctx.ReadDataBool(66UL, false);
        }

        public class WRITER : SerializerState
        {
            public WRITER()
            {
                this.SetStruct(5, 2);
            }

            public long Id
            {
                get => this.ReadDataLong(0UL, 0L);
                set => this.WriteData(0UL, value, 0L);
            }

            public string Platform
            {
                get => this.ReadText(0, null);
                set => this.WriteText(0, value, null);
            }

            public bool IsExclusive
            {
                get => this.ReadDataBool(64UL, false);
                set => this.WriteData(64UL, value, false);
            }

            public string Status
            {
                get => this.ReadText(1, null);
                set => this.WriteText(1, value, null);
            }

            public long RateMultiplier
            {
                get => this.ReadDataLong(128UL, 0L);
                set => this.WriteData(128UL, value, 0);
            }

            public long DailyLimitUsd
            {
                get => this.ReadDataLong(192UL, 0L);
                set => this.WriteData(192UL, value, 0);
            }

            public bool ClaudeCodeOnly
            {
                get => this.ReadDataBool(65UL, false);
                set => this.WriteData(65UL, value, false);
            }

            public long FallbackGroupId
            {
                get => this.ReadDataLong(256UL, 0L);
                set => this.WriteData(256UL, value, 0L);
            }

            public bool ModelRoutingEnabled
            {
                get => this.ReadDataBool(66UL, false);
                set => this.WriteData(66UL, value, false);
            }
        }
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xde259f0bd413a23cUL)]
    public class UpstreamTarget : ICapnpSerializable
    {
        public const UInt64 typeId = 0xde259f0bd413a23cUL;
        void ICapnpSerializable.Deserialize(DeserializerState arg_)
        {
            var reader = READER.create(arg_);
            AccountId = reader.AccountId;
            Platform = reader.Platform;
            BaseUrl = reader.BaseUrl;
            UpstreamPath = reader.UpstreamPath;
            AuthHeaders = reader.AuthHeaders?.ToReadOnlyList(_ => CapnpSerializable.Create<CapnpGen.UpstreamTarget.Header>(_));
            MappedModel = reader.MappedModel;
            Proxy = CapnpSerializable.Create<CapnpGen.ProxyConfig>(reader.Proxy);
            UserId = reader.UserId;
            GroupId = reader.GroupId;
            Billing = CapnpSerializable.Create<CapnpGen.BillingContext>(reader.Billing);
            TlsFingerprint = reader.TlsFingerprint;
            HttpMethod = reader.HttpMethod;
            UpstreamFormat = reader.UpstreamFormat;
            RequestHeaders = reader.RequestHeaders?.ToReadOnlyList(_ => CapnpSerializable.Create<CapnpGen.UpstreamTarget.Header>(_));
            AllowedResponseHeaders = reader.AllowedResponseHeaders;
            WebsocketUrl = reader.WebsocketUrl;
            WebsocketProtocol = reader.WebsocketProtocol;
            TlsFingerprintProfileId = reader.TlsFingerprintProfileId;
            CapabilityFlags = reader.CapabilityFlags;
            MediaOperationId = reader.MediaOperationId;
            UpstreamTaskId = reader.UpstreamTaskId;
            PollingSupported = reader.PollingSupported;
            ContentDownloadSupported = reader.ContentDownloadSupported;
            applyDefaults();
        }

        public void serialize(WRITER writer)
        {
            writer.AccountId = AccountId;
            writer.Platform = Platform;
            writer.BaseUrl = BaseUrl;
            writer.UpstreamPath = UpstreamPath;
            writer.AuthHeaders.Init(AuthHeaders, (_s1, _v1) => _v1?.serialize(_s1));
            writer.MappedModel = MappedModel;
            Proxy?.serialize(writer.Proxy);
            writer.UserId = UserId;
            writer.GroupId = GroupId;
            Billing?.serialize(writer.Billing);
            writer.TlsFingerprint = TlsFingerprint;
            writer.HttpMethod = HttpMethod;
            writer.UpstreamFormat = UpstreamFormat;
            writer.RequestHeaders.Init(RequestHeaders, (_s1, _v1) => _v1?.serialize(_s1));
            writer.AllowedResponseHeaders.Init(AllowedResponseHeaders);
            writer.WebsocketUrl = WebsocketUrl;
            writer.WebsocketProtocol = WebsocketProtocol;
            writer.TlsFingerprintProfileId = TlsFingerprintProfileId;
            writer.CapabilityFlags.Init(CapabilityFlags);
            writer.MediaOperationId = MediaOperationId;
            writer.UpstreamTaskId = UpstreamTaskId;
            writer.PollingSupported = PollingSupported;
            writer.ContentDownloadSupported = ContentDownloadSupported;
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

        public string Platform
        {
            get;
            set;
        }

        public string BaseUrl
        {
            get;
            set;
        }

        public string UpstreamPath
        {
            get;
            set;
        }

        public IReadOnlyList<CapnpGen.UpstreamTarget.Header> AuthHeaders
        {
            get;
            set;
        }

        public string MappedModel
        {
            get;
            set;
        }

        public CapnpGen.ProxyConfig Proxy
        {
            get;
            set;
        }

        public long UserId
        {
            get;
            set;
        }

        public long GroupId
        {
            get;
            set;
        }

        public CapnpGen.BillingContext Billing
        {
            get;
            set;
        }

        public bool TlsFingerprint
        {
            get;
            set;
        }

        public string HttpMethod
        {
            get;
            set;
        }

        public string UpstreamFormat
        {
            get;
            set;
        }

        public IReadOnlyList<CapnpGen.UpstreamTarget.Header> RequestHeaders
        {
            get;
            set;
        }

        public IReadOnlyList<string> AllowedResponseHeaders
        {
            get;
            set;
        }

        public string WebsocketUrl
        {
            get;
            set;
        }

        public string WebsocketProtocol
        {
            get;
            set;
        }

        public string TlsFingerprintProfileId
        {
            get;
            set;
        }

        public IReadOnlyList<string> CapabilityFlags
        {
            get;
            set;
        }

        public string MediaOperationId
        {
            get;
            set;
        }

        public string UpstreamTaskId
        {
            get;
            set;
        }

        public bool PollingSupported
        {
            get;
            set;
        }

        public bool ContentDownloadSupported
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
            public string Platform => ctx.ReadText(0, null);
            public string BaseUrl => ctx.ReadText(1, null);
            public string UpstreamPath => ctx.ReadText(2, null);
            public IReadOnlyList<CapnpGen.UpstreamTarget.Header.READER> AuthHeaders => ctx.ReadList(3).Cast(CapnpGen.UpstreamTarget.Header.READER.create);
            public string MappedModel => ctx.ReadText(4, null);
            public CapnpGen.ProxyConfig.READER Proxy => ctx.ReadStruct(5, CapnpGen.ProxyConfig.READER.create);
            public long UserId => ctx.ReadDataLong(64UL, 0L);
            public long GroupId => ctx.ReadDataLong(128UL, 0L);
            public CapnpGen.BillingContext.READER Billing => ctx.ReadStruct(6, CapnpGen.BillingContext.READER.create);
            public bool TlsFingerprint => ctx.ReadDataBool(192UL, false);
            public string HttpMethod => ctx.ReadText(7, null);
            public string UpstreamFormat => ctx.ReadText(8, null);
            public IReadOnlyList<CapnpGen.UpstreamTarget.Header.READER> RequestHeaders => ctx.ReadList(9).Cast(CapnpGen.UpstreamTarget.Header.READER.create);
            public IReadOnlyList<string> AllowedResponseHeaders => ctx.ReadList(10).CastText2();
            public string WebsocketUrl => ctx.ReadText(11, null);
            public string WebsocketProtocol => ctx.ReadText(12, null);
            public string TlsFingerprintProfileId => ctx.ReadText(13, null);
            public IReadOnlyList<string> CapabilityFlags => ctx.ReadList(14).CastText2();
            public string MediaOperationId => ctx.ReadText(15, null);
            public string UpstreamTaskId => ctx.ReadText(16, null);
            public bool PollingSupported => ctx.ReadDataBool(193UL, false);
            public bool ContentDownloadSupported => ctx.ReadDataBool(194UL, false);
        }

        public class WRITER : SerializerState
        {
            public WRITER()
            {
                this.SetStruct(4, 17);
            }

            public long AccountId
            {
                get => this.ReadDataLong(0UL, 0L);
                set => this.WriteData(0UL, value, 0L);
            }

            public string Platform
            {
                get => this.ReadText(0, null);
                set => this.WriteText(0, value, null);
            }

            public string BaseUrl
            {
                get => this.ReadText(1, null);
                set => this.WriteText(1, value, null);
            }

            public string UpstreamPath
            {
                get => this.ReadText(2, null);
                set => this.WriteText(2, value, null);
            }

            public ListOfStructsSerializer<CapnpGen.UpstreamTarget.Header.WRITER> AuthHeaders
            {
                get => BuildPointer<ListOfStructsSerializer<CapnpGen.UpstreamTarget.Header.WRITER>>(3);
                set => Link(3, value);
            }

            public string MappedModel
            {
                get => this.ReadText(4, null);
                set => this.WriteText(4, value, null);
            }

            public CapnpGen.ProxyConfig.WRITER Proxy
            {
                get => BuildPointer<CapnpGen.ProxyConfig.WRITER>(5);
                set => Link(5, value);
            }

            public long UserId
            {
                get => this.ReadDataLong(64UL, 0L);
                set => this.WriteData(64UL, value, 0L);
            }

            public long GroupId
            {
                get => this.ReadDataLong(128UL, 0L);
                set => this.WriteData(128UL, value, 0L);
            }

            public CapnpGen.BillingContext.WRITER Billing
            {
                get => BuildPointer<CapnpGen.BillingContext.WRITER>(6);
                set => Link(6, value);
            }

            public bool TlsFingerprint
            {
                get => this.ReadDataBool(192UL, false);
                set => this.WriteData(192UL, value, false);
            }

            public string HttpMethod
            {
                get => this.ReadText(7, null);
                set => this.WriteText(7, value, null);
            }

            public string UpstreamFormat
            {
                get => this.ReadText(8, null);
                set => this.WriteText(8, value, null);
            }

            public ListOfStructsSerializer<CapnpGen.UpstreamTarget.Header.WRITER> RequestHeaders
            {
                get => BuildPointer<ListOfStructsSerializer<CapnpGen.UpstreamTarget.Header.WRITER>>(9);
                set => Link(9, value);
            }

            public ListOfTextSerializer AllowedResponseHeaders
            {
                get => BuildPointer<ListOfTextSerializer>(10);
                set => Link(10, value);
            }

            public string WebsocketUrl
            {
                get => this.ReadText(11, null);
                set => this.WriteText(11, value, null);
            }

            public string WebsocketProtocol
            {
                get => this.ReadText(12, null);
                set => this.WriteText(12, value, null);
            }

            public string TlsFingerprintProfileId
            {
                get => this.ReadText(13, null);
                set => this.WriteText(13, value, null);
            }

            public ListOfTextSerializer CapabilityFlags
            {
                get => BuildPointer<ListOfTextSerializer>(14);
                set => Link(14, value);
            }

            public string MediaOperationId
            {
                get => this.ReadText(15, null);
                set => this.WriteText(15, value, null);
            }

            public string UpstreamTaskId
            {
                get => this.ReadText(16, null);
                set => this.WriteText(16, value, null);
            }

            public bool PollingSupported
            {
                get => this.ReadDataBool(193UL, false);
                set => this.WriteData(193UL, value, false);
            }

            public bool ContentDownloadSupported
            {
                get => this.ReadDataBool(194UL, false);
                set => this.WriteData(194UL, value, false);
            }
        }

        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xf9b1b5e00011ed84UL)]
        public class Header : ICapnpSerializable
        {
            public const UInt64 typeId = 0xf9b1b5e00011ed84UL;
            void ICapnpSerializable.Deserialize(DeserializerState arg_)
            {
                var reader = READER.create(arg_);
                Key = reader.Key;
                Value = reader.Value;
                applyDefaults();
            }

            public void serialize(WRITER writer)
            {
                writer.Key = Key;
                writer.Value = Value;
            }

            void ICapnpSerializable.Serialize(SerializerState arg_)
            {
                serialize(arg_.Rewrap<WRITER>());
            }

            public void applyDefaults()
            {
            }

            public string Key
            {
                get;
                set;
            }

            public string Value
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
                public string Key => ctx.ReadText(0, null);
                public string Value => ctx.ReadText(1, null);
            }

            public class WRITER : SerializerState
            {
                public WRITER()
                {
                    this.SetStruct(0, 2);
                }

                public string Key
                {
                    get => this.ReadText(0, null);
                    set => this.WriteText(0, value, null);
                }

                public string Value
                {
                    get => this.ReadText(1, null);
                    set => this.WriteText(1, value, null);
                }
            }
        }
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xc40f5bb1fe60a9fcUL)]
    public class ProxyConfig : ICapnpSerializable
    {
        public const UInt64 typeId = 0xc40f5bb1fe60a9fcUL;
        void ICapnpSerializable.Deserialize(DeserializerState arg_)
        {
            var reader = READER.create(arg_);
            Enabled = reader.Enabled;
            Url = reader.Url;
            Username = reader.Username;
            Password = reader.Password;
            applyDefaults();
        }

        public void serialize(WRITER writer)
        {
            writer.Enabled = Enabled;
            writer.Url = Url;
            writer.Username = Username;
            writer.Password = Password;
        }

        void ICapnpSerializable.Serialize(SerializerState arg_)
        {
            serialize(arg_.Rewrap<WRITER>());
        }

        public void applyDefaults()
        {
        }

        public bool Enabled
        {
            get;
            set;
        }

        public string Url
        {
            get;
            set;
        }

        public string Username
        {
            get;
            set;
        }

        public string Password
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
            public bool Enabled => ctx.ReadDataBool(0UL, false);
            public string Url => ctx.ReadText(0, null);
            public string Username => ctx.ReadText(1, null);
            public string Password => ctx.ReadText(2, null);
        }

        public class WRITER : SerializerState
        {
            public WRITER()
            {
                this.SetStruct(1, 3);
            }

            public bool Enabled
            {
                get => this.ReadDataBool(0UL, false);
                set => this.WriteData(0UL, value, false);
            }

            public string Url
            {
                get => this.ReadText(0, null);
                set => this.WriteText(0, value, null);
            }

            public string Username
            {
                get => this.ReadText(1, null);
                set => this.WriteText(1, value, null);
            }

            public string Password
            {
                get => this.ReadText(2, null);
                set => this.WriteText(2, value, null);
            }
        }
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0x99fca2b053433d50UL)]
    public class BillingContext : ICapnpSerializable
    {
        public const UInt64 typeId = 0x99fca2b053433d50UL;
        void ICapnpSerializable.Deserialize(DeserializerState arg_)
        {
            var reader = READER.create(arg_);
            RateMultiplier = reader.RateMultiplier;
            HoldAmount = reader.HoldAmount;
            HoldHandle = reader.HoldHandle;
            Model = reader.Model;
            UpstreamModel = reader.UpstreamModel;
            InboundEndpoint = reader.InboundEndpoint;
            applyDefaults();
        }

        public void serialize(WRITER writer)
        {
            writer.RateMultiplier = RateMultiplier;
            writer.HoldAmount = HoldAmount;
            writer.HoldHandle = HoldHandle;
            writer.Model = Model;
            writer.UpstreamModel = UpstreamModel;
            writer.InboundEndpoint = InboundEndpoint;
        }

        void ICapnpSerializable.Serialize(SerializerState arg_)
        {
            serialize(arg_.Rewrap<WRITER>());
        }

        public void applyDefaults()
        {
        }

        public long RateMultiplier
        {
            get;
            set;
        }

        public long HoldAmount
        {
            get;
            set;
        }

        public string HoldHandle
        {
            get;
            set;
        }

        public string Model
        {
            get;
            set;
        }

        public string UpstreamModel
        {
            get;
            set;
        }

        public string InboundEndpoint
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
            public long RateMultiplier => ctx.ReadDataLong(0UL, 0L);
            public long HoldAmount => ctx.ReadDataLong(64UL, 0L);
            public string HoldHandle => ctx.ReadText(0, null);
            public string Model => ctx.ReadText(1, null);
            public string UpstreamModel => ctx.ReadText(2, null);
            public string InboundEndpoint => ctx.ReadText(3, null);
        }

        public class WRITER : SerializerState
        {
            public WRITER()
            {
                this.SetStruct(2, 4);
            }

            public long RateMultiplier
            {
                get => this.ReadDataLong(0UL, 0L);
                set => this.WriteData(0UL, value, 0);
            }

            public long HoldAmount
            {
                get => this.ReadDataLong(64UL, 0L);
                set => this.WriteData(64UL, value, 0);
            }

            public string HoldHandle
            {
                get => this.ReadText(0, null);
                set => this.WriteText(0, value, null);
            }

            public string Model
            {
                get => this.ReadText(1, null);
                set => this.WriteText(1, value, null);
            }

            public string UpstreamModel
            {
                get => this.ReadText(2, null);
                set => this.WriteText(2, value, null);
            }

            public string InboundEndpoint
            {
                get => this.ReadText(3, null);
                set => this.WriteText(3, value, null);
            }
        }
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xf14f6d84d47ce994UL)]
    public class AccountProjection : ICapnpSerializable
    {
        public const UInt64 typeId = 0xf14f6d84d47ce994UL;
        void ICapnpSerializable.Deserialize(DeserializerState arg_)
        {
            var reader = READER.create(arg_);
            Id = reader.Id;
            Name = reader.Name;
            Platform = reader.Platform;
            Priority = reader.Priority;
            Concurrency = reader.Concurrency;
            CurrentLoad = reader.CurrentLoad;
            Schedulable = reader.Schedulable;
            RateMultiplier = reader.RateMultiplier;
            LoadFactor = reader.LoadFactor;
            Status = reader.Status;
            RateLimitResetAt = reader.RateLimitResetAt;
            OverloadUntil = reader.OverloadUntil;
            TempUnschedulableUntil = reader.TempUnschedulableUntil;
            SupportedModels = reader.SupportedModels;
            GroupIds = reader.GroupIds;
            applyDefaults();
        }

        public void serialize(WRITER writer)
        {
            writer.Id = Id;
            writer.Name = Name;
            writer.Platform = Platform;
            writer.Priority = Priority;
            writer.Concurrency = Concurrency;
            writer.CurrentLoad = CurrentLoad;
            writer.Schedulable = Schedulable;
            writer.RateMultiplier = RateMultiplier;
            writer.LoadFactor = LoadFactor;
            writer.Status = Status;
            writer.RateLimitResetAt = RateLimitResetAt;
            writer.OverloadUntil = OverloadUntil;
            writer.TempUnschedulableUntil = TempUnschedulableUntil;
            writer.SupportedModels.Init(SupportedModels);
            writer.GroupIds.Init(GroupIds);
        }

        void ICapnpSerializable.Serialize(SerializerState arg_)
        {
            serialize(arg_.Rewrap<WRITER>());
        }

        public void applyDefaults()
        {
        }

        public long Id
        {
            get;
            set;
        }

        public string Name
        {
            get;
            set;
        }

        public string Platform
        {
            get;
            set;
        }

        public int Priority
        {
            get;
            set;
        }

        public int Concurrency
        {
            get;
            set;
        }

        public int CurrentLoad
        {
            get;
            set;
        }

        public bool Schedulable
        {
            get;
            set;
        }

        public long RateMultiplier
        {
            get;
            set;
        }

        public int LoadFactor
        {
            get;
            set;
        }

        public string Status
        {
            get;
            set;
        }

        public long RateLimitResetAt
        {
            get;
            set;
        }

        public long OverloadUntil
        {
            get;
            set;
        }

        public long TempUnschedulableUntil
        {
            get;
            set;
        }

        public IReadOnlyList<string> SupportedModels
        {
            get;
            set;
        }

        public IReadOnlyList<long> GroupIds
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
            public long Id => ctx.ReadDataLong(0UL, 0L);
            public string Name => ctx.ReadText(0, null);
            public string Platform => ctx.ReadText(1, null);
            public int Priority => ctx.ReadDataInt(64UL, 0);
            public int Concurrency => ctx.ReadDataInt(96UL, 0);
            public int CurrentLoad => ctx.ReadDataInt(128UL, 0);
            public bool Schedulable => ctx.ReadDataBool(160UL, false);
            public long RateMultiplier => ctx.ReadDataLong(192UL, 0L);
            public int LoadFactor => ctx.ReadDataInt(256UL, 0);
            public string Status => ctx.ReadText(2, null);
            public long RateLimitResetAt => ctx.ReadDataLong(320UL, 0L);
            public long OverloadUntil => ctx.ReadDataLong(384UL, 0L);
            public long TempUnschedulableUntil => ctx.ReadDataLong(448UL, 0L);
            public IReadOnlyList<string> SupportedModels => ctx.ReadList(3).CastText2();
            public IReadOnlyList<long> GroupIds => ctx.ReadList(4).CastLong();
        }

        public class WRITER : SerializerState
        {
            public WRITER()
            {
                this.SetStruct(8, 5);
            }

            public long Id
            {
                get => this.ReadDataLong(0UL, 0L);
                set => this.WriteData(0UL, value, 0L);
            }

            public string Name
            {
                get => this.ReadText(0, null);
                set => this.WriteText(0, value, null);
            }

            public string Platform
            {
                get => this.ReadText(1, null);
                set => this.WriteText(1, value, null);
            }

            public int Priority
            {
                get => this.ReadDataInt(64UL, 0);
                set => this.WriteData(64UL, value, 0);
            }

            public int Concurrency
            {
                get => this.ReadDataInt(96UL, 0);
                set => this.WriteData(96UL, value, 0);
            }

            public int CurrentLoad
            {
                get => this.ReadDataInt(128UL, 0);
                set => this.WriteData(128UL, value, 0);
            }

            public bool Schedulable
            {
                get => this.ReadDataBool(160UL, false);
                set => this.WriteData(160UL, value, false);
            }

            public long RateMultiplier
            {
                get => this.ReadDataLong(192UL, 0L);
                set => this.WriteData(192UL, value, 0);
            }

            public int LoadFactor
            {
                get => this.ReadDataInt(256UL, 0);
                set => this.WriteData(256UL, value, 0);
            }

            public string Status
            {
                get => this.ReadText(2, null);
                set => this.WriteText(2, value, null);
            }

            public long RateLimitResetAt
            {
                get => this.ReadDataLong(320UL, 0L);
                set => this.WriteData(320UL, value, 0L);
            }

            public long OverloadUntil
            {
                get => this.ReadDataLong(384UL, 0L);
                set => this.WriteData(384UL, value, 0L);
            }

            public long TempUnschedulableUntil
            {
                get => this.ReadDataLong(448UL, 0L);
                set => this.WriteData(448UL, value, 0L);
            }

            public ListOfTextSerializer SupportedModels
            {
                get => BuildPointer<ListOfTextSerializer>(3);
                set => Link(3, value);
            }

            public ListOfPrimitivesSerializer<long> GroupIds
            {
                get => BuildPointer<ListOfPrimitivesSerializer<long>>(4);
                set => Link(4, value);
            }
        }
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0x8525e5e442b845d2UL)]
    public class UsageReport : ICapnpSerializable
    {
        public const UInt64 typeId = 0x8525e5e442b845d2UL;
        void ICapnpSerializable.Deserialize(DeserializerState arg_)
        {
            var reader = READER.create(arg_);
            LeaseToken = reader.LeaseToken;
            RequestId = reader.RequestId;
            ApiKeyId = reader.ApiKeyId;
            UserId = reader.UserId;
            AccountId = reader.AccountId;
            GroupId = reader.GroupId;
            Model = reader.Model;
            UpstreamModel = reader.UpstreamModel;
            InboundEndpoint = reader.InboundEndpoint;
            InputTokens = reader.InputTokens;
            OutputTokens = reader.OutputTokens;
            CacheCreateTokens = reader.CacheCreateTokens;
            CacheReadTokens = reader.CacheReadTokens;
            DurationMs = reader.DurationMs;
            FirstTokenMs = reader.FirstTokenMs;
            Stream = reader.Stream;
            ClientDisconnect = reader.ClientDisconnect;
            ForceCacheBilling = reader.ForceCacheBilling;
            IpAddress = reader.IpAddress;
            UserAgent = reader.UserAgent;
            StatusCode = reader.StatusCode;
            InputImageCount = reader.InputImageCount;
            OutputImageCount = reader.OutputImageCount;
            ImageSize = reader.ImageSize;
            VideoCount = reader.VideoCount;
            VideoResolution = reader.VideoResolution;
            VideoDurationSeconds = reader.VideoDurationSeconds;
            RealtimeDurationMs = reader.RealtimeDurationMs;
            RealtimeFrames = reader.RealtimeFrames;
            DisconnectReason = reader.DisconnectReason;
            ProviderUsageJson = reader.ProviderUsageJson;
            ReasoningTokens = reader.ReasoningTokens;
            ServiceTier = reader.ServiceTier;
            UpstreamEndpoint = reader.UpstreamEndpoint;
            CancellationReason = reader.CancellationReason;
            MediaOperationId = reader.MediaOperationId;
            PricingVersion = reader.PricingVersion;
            applyDefaults();
        }

        public void serialize(WRITER writer)
        {
            writer.LeaseToken = LeaseToken;
            writer.RequestId = RequestId;
            writer.ApiKeyId = ApiKeyId;
            writer.UserId = UserId;
            writer.AccountId = AccountId;
            writer.GroupId = GroupId;
            writer.Model = Model;
            writer.UpstreamModel = UpstreamModel;
            writer.InboundEndpoint = InboundEndpoint;
            writer.InputTokens = InputTokens;
            writer.OutputTokens = OutputTokens;
            writer.CacheCreateTokens = CacheCreateTokens;
            writer.CacheReadTokens = CacheReadTokens;
            writer.DurationMs = DurationMs;
            writer.FirstTokenMs = FirstTokenMs;
            writer.Stream = Stream;
            writer.ClientDisconnect = ClientDisconnect;
            writer.ForceCacheBilling = ForceCacheBilling;
            writer.IpAddress = IpAddress;
            writer.UserAgent = UserAgent;
            writer.StatusCode = StatusCode;
            writer.InputImageCount = InputImageCount;
            writer.OutputImageCount = OutputImageCount;
            writer.ImageSize = ImageSize;
            writer.VideoCount = VideoCount;
            writer.VideoResolution = VideoResolution;
            writer.VideoDurationSeconds = VideoDurationSeconds;
            writer.RealtimeDurationMs = RealtimeDurationMs;
            writer.RealtimeFrames = RealtimeFrames;
            writer.DisconnectReason = DisconnectReason;
            writer.ProviderUsageJson = ProviderUsageJson;
            writer.ReasoningTokens = ReasoningTokens;
            writer.ServiceTier = ServiceTier;
            writer.UpstreamEndpoint = UpstreamEndpoint;
            writer.CancellationReason = CancellationReason;
            writer.MediaOperationId = MediaOperationId;
            writer.PricingVersion = PricingVersion;
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

        public string RequestId
        {
            get;
            set;
        }

        public long ApiKeyId
        {
            get;
            set;
        }

        public long UserId
        {
            get;
            set;
        }

        public long AccountId
        {
            get;
            set;
        }

        public long GroupId
        {
            get;
            set;
        }

        public string Model
        {
            get;
            set;
        }

        public string UpstreamModel
        {
            get;
            set;
        }

        public string InboundEndpoint
        {
            get;
            set;
        }

        public int InputTokens
        {
            get;
            set;
        }

        public int OutputTokens
        {
            get;
            set;
        }

        public int CacheCreateTokens
        {
            get;
            set;
        }

        public int CacheReadTokens
        {
            get;
            set;
        }

        public int DurationMs
        {
            get;
            set;
        }

        public int FirstTokenMs
        {
            get;
            set;
        }

        public bool Stream
        {
            get;
            set;
        }

        public bool ClientDisconnect
        {
            get;
            set;
        }

        public bool ForceCacheBilling
        {
            get;
            set;
        }

        public string IpAddress
        {
            get;
            set;
        }

        public string UserAgent
        {
            get;
            set;
        }

        public int StatusCode
        {
            get;
            set;
        }

        public int InputImageCount
        {
            get;
            set;
        }

        public int OutputImageCount
        {
            get;
            set;
        }

        public string ImageSize
        {
            get;
            set;
        }

        public int VideoCount
        {
            get;
            set;
        }

        public string VideoResolution
        {
            get;
            set;
        }

        public int VideoDurationSeconds
        {
            get;
            set;
        }

        public int RealtimeDurationMs
        {
            get;
            set;
        }

        public int RealtimeFrames
        {
            get;
            set;
        }

        public string DisconnectReason
        {
            get;
            set;
        }

        public string ProviderUsageJson
        {
            get;
            set;
        }

        public int ReasoningTokens
        {
            get;
            set;
        }

        public string ServiceTier
        {
            get;
            set;
        }

        public string UpstreamEndpoint
        {
            get;
            set;
        }

        public string CancellationReason
        {
            get;
            set;
        }

        public string MediaOperationId
        {
            get;
            set;
        }

        public string PricingVersion
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
            public string RequestId => ctx.ReadText(1, null);
            public long ApiKeyId => ctx.ReadDataLong(0UL, 0L);
            public long UserId => ctx.ReadDataLong(64UL, 0L);
            public long AccountId => ctx.ReadDataLong(128UL, 0L);
            public long GroupId => ctx.ReadDataLong(192UL, 0L);
            public string Model => ctx.ReadText(2, null);
            public string UpstreamModel => ctx.ReadText(3, null);
            public string InboundEndpoint => ctx.ReadText(4, null);
            public int InputTokens => ctx.ReadDataInt(256UL, 0);
            public int OutputTokens => ctx.ReadDataInt(288UL, 0);
            public int CacheCreateTokens => ctx.ReadDataInt(320UL, 0);
            public int CacheReadTokens => ctx.ReadDataInt(352UL, 0);
            public int DurationMs => ctx.ReadDataInt(384UL, 0);
            public int FirstTokenMs => ctx.ReadDataInt(416UL, 0);
            public bool Stream => ctx.ReadDataBool(448UL, false);
            public bool ClientDisconnect => ctx.ReadDataBool(449UL, false);
            public bool ForceCacheBilling => ctx.ReadDataBool(450UL, false);
            public string IpAddress => ctx.ReadText(5, null);
            public string UserAgent => ctx.ReadText(6, null);
            public int StatusCode => ctx.ReadDataInt(480UL, 0);
            public int InputImageCount => ctx.ReadDataInt(512UL, 0);
            public int OutputImageCount => ctx.ReadDataInt(544UL, 0);
            public string ImageSize => ctx.ReadText(7, null);
            public int VideoCount => ctx.ReadDataInt(576UL, 0);
            public string VideoResolution => ctx.ReadText(8, null);
            public int VideoDurationSeconds => ctx.ReadDataInt(608UL, 0);
            public int RealtimeDurationMs => ctx.ReadDataInt(640UL, 0);
            public int RealtimeFrames => ctx.ReadDataInt(672UL, 0);
            public string DisconnectReason => ctx.ReadText(9, null);
            public string ProviderUsageJson => ctx.ReadText(10, null);
            public int ReasoningTokens => ctx.ReadDataInt(704UL, 0);
            public string ServiceTier => ctx.ReadText(11, null);
            public string UpstreamEndpoint => ctx.ReadText(12, null);
            public string CancellationReason => ctx.ReadText(13, null);
            public string MediaOperationId => ctx.ReadText(14, null);
            public string PricingVersion => ctx.ReadText(15, null);
        }

        public class WRITER : SerializerState
        {
            public WRITER()
            {
                this.SetStruct(12, 16);
            }

            public string LeaseToken
            {
                get => this.ReadText(0, null);
                set => this.WriteText(0, value, null);
            }

            public string RequestId
            {
                get => this.ReadText(1, null);
                set => this.WriteText(1, value, null);
            }

            public long ApiKeyId
            {
                get => this.ReadDataLong(0UL, 0L);
                set => this.WriteData(0UL, value, 0L);
            }

            public long UserId
            {
                get => this.ReadDataLong(64UL, 0L);
                set => this.WriteData(64UL, value, 0L);
            }

            public long AccountId
            {
                get => this.ReadDataLong(128UL, 0L);
                set => this.WriteData(128UL, value, 0L);
            }

            public long GroupId
            {
                get => this.ReadDataLong(192UL, 0L);
                set => this.WriteData(192UL, value, 0L);
            }

            public string Model
            {
                get => this.ReadText(2, null);
                set => this.WriteText(2, value, null);
            }

            public string UpstreamModel
            {
                get => this.ReadText(3, null);
                set => this.WriteText(3, value, null);
            }

            public string InboundEndpoint
            {
                get => this.ReadText(4, null);
                set => this.WriteText(4, value, null);
            }

            public int InputTokens
            {
                get => this.ReadDataInt(256UL, 0);
                set => this.WriteData(256UL, value, 0);
            }

            public int OutputTokens
            {
                get => this.ReadDataInt(288UL, 0);
                set => this.WriteData(288UL, value, 0);
            }

            public int CacheCreateTokens
            {
                get => this.ReadDataInt(320UL, 0);
                set => this.WriteData(320UL, value, 0);
            }

            public int CacheReadTokens
            {
                get => this.ReadDataInt(352UL, 0);
                set => this.WriteData(352UL, value, 0);
            }

            public int DurationMs
            {
                get => this.ReadDataInt(384UL, 0);
                set => this.WriteData(384UL, value, 0);
            }

            public int FirstTokenMs
            {
                get => this.ReadDataInt(416UL, 0);
                set => this.WriteData(416UL, value, 0);
            }

            public bool Stream
            {
                get => this.ReadDataBool(448UL, false);
                set => this.WriteData(448UL, value, false);
            }

            public bool ClientDisconnect
            {
                get => this.ReadDataBool(449UL, false);
                set => this.WriteData(449UL, value, false);
            }

            public bool ForceCacheBilling
            {
                get => this.ReadDataBool(450UL, false);
                set => this.WriteData(450UL, value, false);
            }

            public string IpAddress
            {
                get => this.ReadText(5, null);
                set => this.WriteText(5, value, null);
            }

            public string UserAgent
            {
                get => this.ReadText(6, null);
                set => this.WriteText(6, value, null);
            }

            public int StatusCode
            {
                get => this.ReadDataInt(480UL, 0);
                set => this.WriteData(480UL, value, 0);
            }

            public int InputImageCount
            {
                get => this.ReadDataInt(512UL, 0);
                set => this.WriteData(512UL, value, 0);
            }

            public int OutputImageCount
            {
                get => this.ReadDataInt(544UL, 0);
                set => this.WriteData(544UL, value, 0);
            }

            public string ImageSize
            {
                get => this.ReadText(7, null);
                set => this.WriteText(7, value, null);
            }

            public int VideoCount
            {
                get => this.ReadDataInt(576UL, 0);
                set => this.WriteData(576UL, value, 0);
            }

            public string VideoResolution
            {
                get => this.ReadText(8, null);
                set => this.WriteText(8, value, null);
            }

            public int VideoDurationSeconds
            {
                get => this.ReadDataInt(608UL, 0);
                set => this.WriteData(608UL, value, 0);
            }

            public int RealtimeDurationMs
            {
                get => this.ReadDataInt(640UL, 0);
                set => this.WriteData(640UL, value, 0);
            }

            public int RealtimeFrames
            {
                get => this.ReadDataInt(672UL, 0);
                set => this.WriteData(672UL, value, 0);
            }

            public string DisconnectReason
            {
                get => this.ReadText(9, null);
                set => this.WriteText(9, value, null);
            }

            public string ProviderUsageJson
            {
                get => this.ReadText(10, null);
                set => this.WriteText(10, value, null);
            }

            public int ReasoningTokens
            {
                get => this.ReadDataInt(704UL, 0);
                set => this.WriteData(704UL, value, 0);
            }

            public string ServiceTier
            {
                get => this.ReadText(11, null);
                set => this.WriteText(11, value, null);
            }

            public string UpstreamEndpoint
            {
                get => this.ReadText(12, null);
                set => this.WriteText(12, value, null);
            }

            public string CancellationReason
            {
                get => this.ReadText(13, null);
                set => this.WriteText(13, value, null);
            }

            public string MediaOperationId
            {
                get => this.ReadText(14, null);
                set => this.WriteText(14, value, null);
            }

            public string PricingVersion
            {
                get => this.ReadText(15, null);
                set => this.WriteText(15, value, null);
            }
        }
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xa6d2d0f72b54b389UL)]
    public class ErrorReport : ICapnpSerializable
    {
        public const UInt64 typeId = 0xa6d2d0f72b54b389UL;
        void ICapnpSerializable.Deserialize(DeserializerState arg_)
        {
            var reader = READER.create(arg_);
            AccountId = reader.AccountId;
            StatusCode = reader.StatusCode;
            RetryAfterMs = reader.RetryAfterMs;
            RequestId = reader.RequestId;
            ErrorMessage = reader.ErrorMessage;
            applyDefaults();
        }

        public void serialize(WRITER writer)
        {
            writer.AccountId = AccountId;
            writer.StatusCode = StatusCode;
            writer.RetryAfterMs = RetryAfterMs;
            writer.RequestId = RequestId;
            writer.ErrorMessage = ErrorMessage;
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

        public int StatusCode
        {
            get;
            set;
        }

        public int RetryAfterMs
        {
            get;
            set;
        }

        public string RequestId
        {
            get;
            set;
        }

        public string ErrorMessage
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
            public int StatusCode => ctx.ReadDataInt(64UL, 0);
            public int RetryAfterMs => ctx.ReadDataInt(96UL, 0);
            public string RequestId => ctx.ReadText(0, null);
            public string ErrorMessage => ctx.ReadText(1, null);
        }

        public class WRITER : SerializerState
        {
            public WRITER()
            {
                this.SetStruct(2, 2);
            }

            public long AccountId
            {
                get => this.ReadDataLong(0UL, 0L);
                set => this.WriteData(0UL, value, 0L);
            }

            public int StatusCode
            {
                get => this.ReadDataInt(64UL, 0);
                set => this.WriteData(64UL, value, 0);
            }

            public int RetryAfterMs
            {
                get => this.ReadDataInt(96UL, 0);
                set => this.WriteData(96UL, value, 0);
            }

            public string RequestId
            {
                get => this.ReadText(0, null);
                set => this.WriteText(0, value, null);
            }

            public string ErrorMessage
            {
                get => this.ReadText(1, null);
                set => this.WriteText(1, value, null);
            }
        }
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0x83192604fd74d20eUL)]
    public class ModelRoute : ICapnpSerializable
    {
        public const UInt64 typeId = 0x83192604fd74d20eUL;
        void ICapnpSerializable.Deserialize(DeserializerState arg_)
        {
            var reader = READER.create(arg_);
            Pattern = reader.Pattern;
            AccountIds = reader.AccountIds;
            applyDefaults();
        }

        public void serialize(WRITER writer)
        {
            writer.Pattern = Pattern;
            writer.AccountIds.Init(AccountIds);
        }

        void ICapnpSerializable.Serialize(SerializerState arg_)
        {
            serialize(arg_.Rewrap<WRITER>());
        }

        public void applyDefaults()
        {
        }

        public string Pattern
        {
            get;
            set;
        }

        public IReadOnlyList<long> AccountIds
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
            public string Pattern => ctx.ReadText(0, null);
            public IReadOnlyList<long> AccountIds => ctx.ReadList(1).CastLong();
        }

        public class WRITER : SerializerState
        {
            public WRITER()
            {
                this.SetStruct(0, 2);
            }

            public string Pattern
            {
                get => this.ReadText(0, null);
                set => this.WriteText(0, value, null);
            }

            public ListOfPrimitivesSerializer<long> AccountIds
            {
                get => BuildPointer<ListOfPrimitivesSerializer<long>>(1);
                set => Link(1, value);
            }
        }
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xb8bb6df45db0c669UL)]
    public class GroupConfig : ICapnpSerializable
    {
        public const UInt64 typeId = 0xb8bb6df45db0c669UL;
        void ICapnpSerializable.Deserialize(DeserializerState arg_)
        {
            var reader = READER.create(arg_);
            Id = reader.Id;
            Platform = reader.Platform;
            RateMultiplier = reader.RateMultiplier;
            ModelRoutingEnabled = reader.ModelRoutingEnabled;
            ModelRoutes = reader.ModelRoutes?.ToReadOnlyList(_ => CapnpSerializable.Create<CapnpGen.ModelRoute>(_));
            ClaudeCodeOnly = reader.ClaudeCodeOnly;
            FallbackGroupId = reader.FallbackGroupId;
            RpmLimit = reader.RpmLimit;
            PeakMultiplier = reader.PeakMultiplier;
            PeakStartHour = reader.PeakStartHour;
            PeakEndHour = reader.PeakEndHour;
            applyDefaults();
        }

        public void serialize(WRITER writer)
        {
            writer.Id = Id;
            writer.Platform = Platform;
            writer.RateMultiplier = RateMultiplier;
            writer.ModelRoutingEnabled = ModelRoutingEnabled;
            writer.ModelRoutes.Init(ModelRoutes, (_s1, _v1) => _v1?.serialize(_s1));
            writer.ClaudeCodeOnly = ClaudeCodeOnly;
            writer.FallbackGroupId = FallbackGroupId;
            writer.RpmLimit = RpmLimit;
            writer.PeakMultiplier = PeakMultiplier;
            writer.PeakStartHour = PeakStartHour;
            writer.PeakEndHour = PeakEndHour;
        }

        void ICapnpSerializable.Serialize(SerializerState arg_)
        {
            serialize(arg_.Rewrap<WRITER>());
        }

        public void applyDefaults()
        {
        }

        public long Id
        {
            get;
            set;
        }

        public string Platform
        {
            get;
            set;
        }

        public long RateMultiplier
        {
            get;
            set;
        }

        public bool ModelRoutingEnabled
        {
            get;
            set;
        }

        public IReadOnlyList<CapnpGen.ModelRoute> ModelRoutes
        {
            get;
            set;
        }

        public bool ClaudeCodeOnly
        {
            get;
            set;
        }

        public long FallbackGroupId
        {
            get;
            set;
        }

        public int RpmLimit
        {
            get;
            set;
        }

        public long PeakMultiplier
        {
            get;
            set;
        }

        public int PeakStartHour
        {
            get;
            set;
        }

        public int PeakEndHour
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
            public long Id => ctx.ReadDataLong(0UL, 0L);
            public string Platform => ctx.ReadText(0, null);
            public long RateMultiplier => ctx.ReadDataLong(64UL, 0L);
            public bool ModelRoutingEnabled => ctx.ReadDataBool(128UL, false);
            public IReadOnlyList<CapnpGen.ModelRoute.READER> ModelRoutes => ctx.ReadList(1).Cast(CapnpGen.ModelRoute.READER.create);
            public bool ClaudeCodeOnly => ctx.ReadDataBool(129UL, false);
            public long FallbackGroupId => ctx.ReadDataLong(192UL, 0L);
            public int RpmLimit => ctx.ReadDataInt(160UL, 0);
            public long PeakMultiplier => ctx.ReadDataLong(256UL, 0L);
            public int PeakStartHour => ctx.ReadDataInt(320UL, 0);
            public int PeakEndHour => ctx.ReadDataInt(352UL, 0);
        }

        public class WRITER : SerializerState
        {
            public WRITER()
            {
                this.SetStruct(6, 2);
            }

            public long Id
            {
                get => this.ReadDataLong(0UL, 0L);
                set => this.WriteData(0UL, value, 0L);
            }

            public string Platform
            {
                get => this.ReadText(0, null);
                set => this.WriteText(0, value, null);
            }

            public long RateMultiplier
            {
                get => this.ReadDataLong(64UL, 0L);
                set => this.WriteData(64UL, value, 0);
            }

            public bool ModelRoutingEnabled
            {
                get => this.ReadDataBool(128UL, false);
                set => this.WriteData(128UL, value, false);
            }

            public ListOfStructsSerializer<CapnpGen.ModelRoute.WRITER> ModelRoutes
            {
                get => BuildPointer<ListOfStructsSerializer<CapnpGen.ModelRoute.WRITER>>(1);
                set => Link(1, value);
            }

            public bool ClaudeCodeOnly
            {
                get => this.ReadDataBool(129UL, false);
                set => this.WriteData(129UL, value, false);
            }

            public long FallbackGroupId
            {
                get => this.ReadDataLong(192UL, 0L);
                set => this.WriteData(192UL, value, 0L);
            }

            public int RpmLimit
            {
                get => this.ReadDataInt(160UL, 0);
                set => this.WriteData(160UL, value, 0);
            }

            public long PeakMultiplier
            {
                get => this.ReadDataLong(256UL, 0L);
                set => this.WriteData(256UL, value, 0);
            }

            public int PeakStartHour
            {
                get => this.ReadDataInt(320UL, 0);
                set => this.WriteData(320UL, value, 0);
            }

            public int PeakEndHour
            {
                get => this.ReadDataInt(352UL, 0);
                set => this.WriteData(352UL, value, 0);
            }
        }
    }
}