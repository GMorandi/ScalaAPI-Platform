using Capnp;
using Capnp.Rpc;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CapnpGen
{
    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xb4596d252d437f27UL), Proxy(typeof(InvalidationStream_Proxy)), Skeleton(typeof(InvalidationStream_Skeleton))]
    public interface IInvalidationStream : IDisposable
    {
        Task<CapnpGen.InvalidationEvent> Subscribe(string gatewayId, CancellationToken cancellationToken_ = default);
        Task<IReadOnlyList<CapnpGen.InvalidationStream.VersionEntry>> Resync(IReadOnlyList<CapnpGen.InvalidationStream.VersionEntry> versions, CancellationToken cancellationToken_ = default);
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xb4596d252d437f27UL)]
    public class InvalidationStream_Proxy : Proxy, IInvalidationStream
    {
        public async Task<CapnpGen.InvalidationEvent> Subscribe(string gatewayId, CancellationToken cancellationToken_ = default)
        {
            var in_ = SerializerState.CreateForRpc<CapnpGen.InvalidationStream.Params_Subscribe.WRITER>();
            var arg_ = new CapnpGen.InvalidationStream.Params_Subscribe()
            {GatewayId = gatewayId};
            arg_?.serialize(in_);
            using (var d_ = await Call(12995538206194892583UL, 0, in_.Rewrap<DynamicSerializerState>(), false, cancellationToken_).WhenReturned)
            {
                var r_ = CapnpSerializable.Create<CapnpGen.InvalidationStream.Result_Subscribe>(d_);
                return (r_.Stream);
            }
        }

        public async Task<IReadOnlyList<CapnpGen.InvalidationStream.VersionEntry>> Resync(IReadOnlyList<CapnpGen.InvalidationStream.VersionEntry> versions, CancellationToken cancellationToken_ = default)
        {
            var in_ = SerializerState.CreateForRpc<CapnpGen.InvalidationStream.Params_Resync.WRITER>();
            var arg_ = new CapnpGen.InvalidationStream.Params_Resync()
            {Versions = versions};
            arg_?.serialize(in_);
            using (var d_ = await Call(12995538206194892583UL, 1, in_.Rewrap<DynamicSerializerState>(), false, cancellationToken_).WhenReturned)
            {
                var r_ = CapnpSerializable.Create<CapnpGen.InvalidationStream.Result_Resync>(d_);
                return (r_.Stale);
            }
        }
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xb4596d252d437f27UL)]
    public class InvalidationStream_Skeleton : Skeleton<IInvalidationStream>
    {
        public InvalidationStream_Skeleton()
        {
            SetMethodTable(Subscribe, Resync);
        }

        public override ulong InterfaceId => 12995538206194892583UL;
        Task<AnswerOrCounterquestion> Subscribe(DeserializerState d_, CancellationToken cancellationToken_)
        {
            using (d_)
            {
                var in_ = CapnpSerializable.Create<CapnpGen.InvalidationStream.Params_Subscribe>(d_);
                return Impatient.MaybeTailCall(Impl.Subscribe(in_.GatewayId, cancellationToken_), stream =>
                {
                    var s_ = SerializerState.CreateForRpc<CapnpGen.InvalidationStream.Result_Subscribe.WRITER>();
                    var r_ = new CapnpGen.InvalidationStream.Result_Subscribe{Stream = stream};
                    r_.serialize(s_);
                    return s_;
                }

                );
            }
        }

        Task<AnswerOrCounterquestion> Resync(DeserializerState d_, CancellationToken cancellationToken_)
        {
            using (d_)
            {
                var in_ = CapnpSerializable.Create<CapnpGen.InvalidationStream.Params_Resync>(d_);
                return Impatient.MaybeTailCall(Impl.Resync(in_.Versions, cancellationToken_), stale =>
                {
                    var s_ = SerializerState.CreateForRpc<CapnpGen.InvalidationStream.Result_Resync.WRITER>();
                    var r_ = new CapnpGen.InvalidationStream.Result_Resync{Stale = stale};
                    r_.serialize(s_);
                    return s_;
                }

                );
            }
        }
    }

    public static class InvalidationStream
    {
        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0x93234e35b0a42c9fUL)]
        public class VersionEntry : ICapnpSerializable
        {
            public const UInt64 typeId = 0x93234e35b0a42c9fUL;
            void ICapnpSerializable.Deserialize(DeserializerState arg_)
            {
                var reader = READER.create(arg_);
                EntityType = reader.EntityType;
                EntityKey = reader.EntityKey;
                Version = reader.Version;
                applyDefaults();
            }

            public void serialize(WRITER writer)
            {
                writer.EntityType = EntityType;
                writer.EntityKey = EntityKey;
                writer.Version = Version;
            }

            void ICapnpSerializable.Serialize(SerializerState arg_)
            {
                serialize(arg_.Rewrap<WRITER>());
            }

            public void applyDefaults()
            {
            }

            public string EntityType
            {
                get;
                set;
            }

            public string EntityKey
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
                public string EntityType => ctx.ReadText(0, null);
                public string EntityKey => ctx.ReadText(1, null);
                public long Version => ctx.ReadDataLong(0UL, 0L);
            }

            public class WRITER : SerializerState
            {
                public WRITER()
                {
                    this.SetStruct(1, 2);
                }

                public string EntityType
                {
                    get => this.ReadText(0, null);
                    set => this.WriteText(0, value, null);
                }

                public string EntityKey
                {
                    get => this.ReadText(1, null);
                    set => this.WriteText(1, value, null);
                }

                public long Version
                {
                    get => this.ReadDataLong(0UL, 0L);
                    set => this.WriteData(0UL, value, 0L);
                }
            }
        }

        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xae45a3b02d4819c3UL)]
        public class Params_Subscribe : ICapnpSerializable
        {
            public const UInt64 typeId = 0xae45a3b02d4819c3UL;
            void ICapnpSerializable.Deserialize(DeserializerState arg_)
            {
                var reader = READER.create(arg_);
                GatewayId = reader.GatewayId;
                applyDefaults();
            }

            public void serialize(WRITER writer)
            {
                writer.GatewayId = GatewayId;
            }

            void ICapnpSerializable.Serialize(SerializerState arg_)
            {
                serialize(arg_.Rewrap<WRITER>());
            }

            public void applyDefaults()
            {
            }

            public string GatewayId
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
                public string GatewayId => ctx.ReadText(0, null);
            }

            public class WRITER : SerializerState
            {
                public WRITER()
                {
                    this.SetStruct(0, 1);
                }

                public string GatewayId
                {
                    get => this.ReadText(0, null);
                    set => this.WriteText(0, value, null);
                }
            }
        }

        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xf61d80a80e235272UL)]
        public class Result_Subscribe : ICapnpSerializable
        {
            public const UInt64 typeId = 0xf61d80a80e235272UL;
            void ICapnpSerializable.Deserialize(DeserializerState arg_)
            {
                var reader = READER.create(arg_);
                Stream = CapnpSerializable.Create<CapnpGen.InvalidationEvent>(reader.Stream);
                applyDefaults();
            }

            public void serialize(WRITER writer)
            {
                Stream?.serialize(writer.Stream);
            }

            void ICapnpSerializable.Serialize(SerializerState arg_)
            {
                serialize(arg_.Rewrap<WRITER>());
            }

            public void applyDefaults()
            {
            }

            public CapnpGen.InvalidationEvent Stream
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
                public CapnpGen.InvalidationEvent.READER Stream => ctx.ReadStruct(0, CapnpGen.InvalidationEvent.READER.create);
            }

            public class WRITER : SerializerState
            {
                public WRITER()
                {
                    this.SetStruct(0, 1);
                }

                public CapnpGen.InvalidationEvent.WRITER Stream
                {
                    get => BuildPointer<CapnpGen.InvalidationEvent.WRITER>(0);
                    set => Link(0, value);
                }
            }
        }

        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0x9a9f8be31b5f98b0UL)]
        public class Params_Resync : ICapnpSerializable
        {
            public const UInt64 typeId = 0x9a9f8be31b5f98b0UL;
            void ICapnpSerializable.Deserialize(DeserializerState arg_)
            {
                var reader = READER.create(arg_);
                Versions = reader.Versions?.ToReadOnlyList(_ => CapnpSerializable.Create<CapnpGen.InvalidationStream.VersionEntry>(_));
                applyDefaults();
            }

            public void serialize(WRITER writer)
            {
                writer.Versions.Init(Versions, (_s1, _v1) => _v1?.serialize(_s1));
            }

            void ICapnpSerializable.Serialize(SerializerState arg_)
            {
                serialize(arg_.Rewrap<WRITER>());
            }

            public void applyDefaults()
            {
            }

            public IReadOnlyList<CapnpGen.InvalidationStream.VersionEntry> Versions
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
                public IReadOnlyList<CapnpGen.InvalidationStream.VersionEntry.READER> Versions => ctx.ReadList(0).Cast(CapnpGen.InvalidationStream.VersionEntry.READER.create);
            }

            public class WRITER : SerializerState
            {
                public WRITER()
                {
                    this.SetStruct(0, 1);
                }

                public ListOfStructsSerializer<CapnpGen.InvalidationStream.VersionEntry.WRITER> Versions
                {
                    get => BuildPointer<ListOfStructsSerializer<CapnpGen.InvalidationStream.VersionEntry.WRITER>>(0);
                    set => Link(0, value);
                }
            }
        }

        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xcb3e74ce8397b34dUL)]
        public class Result_Resync : ICapnpSerializable
        {
            public const UInt64 typeId = 0xcb3e74ce8397b34dUL;
            void ICapnpSerializable.Deserialize(DeserializerState arg_)
            {
                var reader = READER.create(arg_);
                Stale = reader.Stale?.ToReadOnlyList(_ => CapnpSerializable.Create<CapnpGen.InvalidationStream.VersionEntry>(_));
                applyDefaults();
            }

            public void serialize(WRITER writer)
            {
                writer.Stale.Init(Stale, (_s1, _v1) => _v1?.serialize(_s1));
            }

            void ICapnpSerializable.Serialize(SerializerState arg_)
            {
                serialize(arg_.Rewrap<WRITER>());
            }

            public void applyDefaults()
            {
            }

            public IReadOnlyList<CapnpGen.InvalidationStream.VersionEntry> Stale
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
                public IReadOnlyList<CapnpGen.InvalidationStream.VersionEntry.READER> Stale => ctx.ReadList(0).Cast(CapnpGen.InvalidationStream.VersionEntry.READER.create);
            }

            public class WRITER : SerializerState
            {
                public WRITER()
                {
                    this.SetStruct(0, 1);
                }

                public ListOfStructsSerializer<CapnpGen.InvalidationStream.VersionEntry.WRITER> Stale
                {
                    get => BuildPointer<ListOfStructsSerializer<CapnpGen.InvalidationStream.VersionEntry.WRITER>>(0);
                    set => Link(0, value);
                }
            }
        }
    }

    [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xbd255cf66f3f5f81UL)]
    public class InvalidationEvent : ICapnpSerializable
    {
        public const UInt64 typeId = 0xbd255cf66f3f5f81UL;
        void ICapnpSerializable.Deserialize(DeserializerState arg_)
        {
            var reader = READER.create(arg_);
            TheEntityType = reader.TheEntityType;
            EntityKey = reader.EntityKey;
            Version = reader.Version;
            TheKind = reader.TheKind;
            applyDefaults();
        }

        public void serialize(WRITER writer)
        {
            writer.TheEntityType = TheEntityType;
            writer.EntityKey = EntityKey;
            writer.Version = Version;
            writer.TheKind = TheKind;
        }

        void ICapnpSerializable.Serialize(SerializerState arg_)
        {
            serialize(arg_.Rewrap<WRITER>());
        }

        public void applyDefaults()
        {
        }

        public CapnpGen.InvalidationEvent.EntityType TheEntityType
        {
            get;
            set;
        }

        public string EntityKey
        {
            get;
            set;
        }

        public long Version
        {
            get;
            set;
        }

        public CapnpGen.InvalidationEvent.Kind TheKind
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
            public CapnpGen.InvalidationEvent.EntityType TheEntityType => (CapnpGen.InvalidationEvent.EntityType)ctx.ReadDataUShort(0UL, (ushort)0);
            public string EntityKey => ctx.ReadText(0, null);
            public long Version => ctx.ReadDataLong(64UL, 0L);
            public CapnpGen.InvalidationEvent.Kind TheKind => (CapnpGen.InvalidationEvent.Kind)ctx.ReadDataUShort(16UL, (ushort)0);
        }

        public class WRITER : SerializerState
        {
            public WRITER()
            {
                this.SetStruct(2, 1);
            }

            public CapnpGen.InvalidationEvent.EntityType TheEntityType
            {
                get => (CapnpGen.InvalidationEvent.EntityType)this.ReadDataUShort(0UL, (ushort)0);
                set => this.WriteData(0UL, (ushort)value, (ushort)0);
            }

            public string EntityKey
            {
                get => this.ReadText(0, null);
                set => this.WriteText(0, value, null);
            }

            public long Version
            {
                get => this.ReadDataLong(64UL, 0L);
                set => this.WriteData(64UL, value, 0L);
            }

            public CapnpGen.InvalidationEvent.Kind TheKind
            {
                get => (CapnpGen.InvalidationEvent.Kind)this.ReadDataUShort(16UL, (ushort)0);
                set => this.WriteData(16UL, (ushort)value, (ushort)0);
            }
        }

        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xa86800959492fee6UL)]
        public enum EntityType : ushort
        {
            apiKey,
            account,
            @group,
            user,
            config
        }

        [System.CodeDom.Compiler.GeneratedCode("capnpc-csharp", "1.3.0.0"), TypeId(0xb4fe61cc5d38c466UL)]
        public enum Kind : ushort
        {
            evict,
            upsert,
            delete
        }
    }
}