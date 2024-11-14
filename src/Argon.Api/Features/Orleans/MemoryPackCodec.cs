namespace Argon.Api.Features.Orleans;

using System.Collections.Concurrent;
using System.Reflection;
using global::Orleans.Serialization;
using global::Orleans.Serialization.Buffers;
using global::Orleans.Serialization.Buffers.Adaptors;
using global::Orleans.Serialization.Cloning;
using global::Orleans.Serialization.Codecs;
using global::Orleans.Serialization.Serializers;
using global::Orleans.Serialization.Utilities.Internal;
using global::Orleans.Serialization.WireProtocol;
using Microsoft.Extensions.Options;

public static class SerializationHostingExtensions
{
    private static readonly ServiceDescriptor ServiceDescriptor = new(typeof(MemoryPackCodec), typeof(MemoryPackCodec));

    public static ISerializerBuilder AddMemoryPackSerializer(this ISerializerBuilder serializerBuilder, Func<Type, bool> isSerializable = null,
        Func<Type, bool> isCopyable = null, MessagePackSerializerOptions messagePackSerializerOptions = null) =>
        serializerBuilder.AddMemoryPackSerializer(isSerializable, isCopyable, optionsBuilder => optionsBuilder.Configure(options =>
        {
            if (messagePackSerializerOptions is not null) options.SerializerOptions = messagePackSerializerOptions;
        }));

    public static ISerializerBuilder AddMemoryPackSerializer(this ISerializerBuilder serializerBuilder, Func<Type, bool> isSerializable,
        Func<Type, bool> isCopyable, Action<OptionsBuilder<MemoryPackCodecOptions>> configureOptions = null)
    {
        var services = serializerBuilder.Services;
        if (configureOptions != null)
            configureOptions(services.AddOptions<MemoryPackCodecOptions>());

        if (isSerializable != null)
        {
            services.AddSingleton<ICodecSelector>(new DelegateCodecSelector
            {
                CodecName               = MemoryPackCodec.WellKnownAlias,
                IsSupportedTypeDelegate = isSerializable
            });
        }

        if (isCopyable != null)
        {
            services.AddSingleton<ICopierSelector>(new DelegateCopierSelector
            {
                CopierName              = MemoryPackCodec.WellKnownAlias,
                IsSupportedTypeDelegate = isCopyable
            });
        }

        if (!services.Contains(ServiceDescriptor))
        {
            services.AddSingleton<MemoryPackCodec>();
            services.AddFromExisting<IGeneralizedCodec, MemoryPackCodec>();
            services.AddFromExisting<IGeneralizedCopier, MemoryPackCodec>();
            services.AddFromExisting<ITypeFilter, MemoryPackCodec>();
            serializerBuilder.Configure(options => options.WellKnownTypeAliases[MemoryPackCodec.WellKnownAlias] = typeof(MemoryPackCodec));
        }

        return serializerBuilder;
    }
}

/// <summary>
///     Options for <see cref="MessagePackCodec" />.
/// </summary>
public class MemoryPackCodecOptions
{
    /// <summary>
    ///     Gets or sets the <see cref="MessagePackSerializerOptions" />.
    /// </summary>
    public MessagePackSerializerOptions SerializerOptions { get; set; } = MessagePackSerializerOptions.Standard;

    /// <summary>
    ///     Get or sets flag that allows the use of <see cref="DataContractAttribute" /> marked contracts for
    ///     MessagePackSerializer.
    /// </summary>
    public bool AllowDataContractAttributes { get; set; }

    /// <summary>
    ///     Gets or sets a delegate used to determine if a type is supported by the MessagePack serializer for serialization
    ///     and deserialization.
    /// </summary>
    public Func<Type, bool?> IsSerializableType { get; set; }

    /// <summary>
    ///     Gets or sets a delegate used to determine if a type is supported by the MessagePack serializer for copying.
    /// </summary>
    public Func<Type, bool?> IsCopyableType { get; set; }
}

/// <summary>
///     A serialization codec which uses <see cref="MemoryPackSerializer" />.
/// </summary>
/// <remarks>
///     MemoryPack codec performs slightly worse than default Orleans serializer, if performance is critical for your
///     application, consider using default serialization.
/// </remarks>
[Alias(WellKnownAlias)]
public class MemoryPackCodec : IGeneralizedCodec, IGeneralizedCopier, ITypeFilter
{
    /// <summary>
    ///     The well-known type alias for this codec.
    /// </summary>
    public const string WellKnownAlias = "mempack";
    private static readonly ConcurrentDictionary<Type, bool> SupportedTypes = new();

    private static readonly Type                   SelfType = typeof(MemoryPackCodec);
    private readonly        ICopierSelector[]      _copyableTypeSelectors;
    private readonly        MemoryPackCodecOptions _options;

    private readonly ICodecSelector[] _serializableTypeSelectors;

    /// <summary>
    ///     Initializes a new instance of the <see cref="MemoryPackCodec" /> class.
    /// </summary>
    /// <param name="serializableTypeSelectors">Filters used to indicate which types should be serialized by this codec.</param>
    /// <param name="copyableTypeSelectors">Filters used to indicate which types should be copied by this codec.</param>
    /// <param name="options">The MemoryPack codec options.</param>
    public MemoryPackCodec(IEnumerable<ICodecSelector> serializableTypeSelectors, IEnumerable<ICopierSelector> copyableTypeSelectors,
        IOptions<MemoryPackCodecOptions> options)
    {
        _serializableTypeSelectors =
            serializableTypeSelectors.Where(t => string.Equals(t.CodecName, WellKnownAlias, StringComparison.Ordinal)).ToArray();
        _copyableTypeSelectors = copyableTypeSelectors.Where(t => string.Equals(t.CopierName, WellKnownAlias, StringComparison.Ordinal)).ToArray();
        _options               = options.Value;
    }

    /// <inheritdoc />
    void IFieldCodec.WriteField<TBufferWriter>(ref Writer<TBufferWriter> writer, uint fieldIdDelta, Type expectedType, object value)
    {
        if (ReferenceCodec.TryWriteReferenceField(ref writer, fieldIdDelta, expectedType, value)) return;

        // The schema type when serializing the field is the type of the codec.
        writer.WriteFieldHeader(fieldIdDelta, expectedType, SelfType, WireType.TagDelimited);

        // Write the type name
        ReferenceCodec.MarkValueField(writer.Session);
        writer.WriteFieldHeaderExpected(0, WireType.LengthPrefixed);
        writer.Session.TypeCodec.WriteLengthPrefixed(ref writer, value.GetType());

        var bufferWriter = new BufferWriterBox<PooledBuffer>(new PooledBuffer());
        try
        {
            MemoryPackSerializer.Serialize(bufferWriter, value);

            ReferenceCodec.MarkValueField(writer.Session);
            writer.WriteFieldHeaderExpected(1, WireType.LengthPrefixed);
            writer.WriteVarUInt32((uint)bufferWriter.Value.Length);
            bufferWriter.Value.CopyTo(ref writer);
        }
        finally
        {
            bufferWriter.Value.Dispose();
        }

        writer.WriteEndObject();
    }

    /// <inheritdoc />
    object IFieldCodec.ReadValue<TInput>(ref Reader<TInput> reader, Field field)
    {
        if (field.IsReference) return ReferenceCodec.ReadReference(ref reader, field.FieldType);

        field.EnsureWireTypeTagDelimited();

        var    placeholderReferenceId = ReferenceCodec.CreateRecordPlaceholder(reader.Session);
        object result                 = null;
        Type   type                   = null;
        uint   fieldId                = 0;
        while (true)
        {
            var header = reader.ReadFieldHeader();
            if (header.IsEndBaseOrEndObject) break;

            fieldId += header.FieldIdDelta;
            switch (fieldId)
            {
                case 0:
                    ReferenceCodec.MarkValueField(reader.Session);
                    type = reader.Session.TypeCodec.ReadLengthPrefixed(ref reader);
                    break;
                case 1:
                    if (type is null) ThrowTypeFieldMissing();

                    ReferenceCodec.MarkValueField(reader.Session);
                    var length = reader.ReadVarUInt32();

                    var bufferWriter = new BufferWriterBox<PooledBuffer>(new PooledBuffer());
                    try
                    {
                        reader.ReadBytes(ref bufferWriter, (int)length);
                        result = MemoryPackSerializer.Deserialize(type, bufferWriter.Value.AsReadOnlySequence());
                    }
                    finally
                    {
                        bufferWriter.Value.Dispose();
                    }

                    break;
                default:
                    reader.ConsumeUnknownField(header);
                    break;
            }
        }

        ReferenceCodec.RecordObject(reader.Session, result, placeholderReferenceId);
        return result;
    }

    /// <inheritdoc />
    bool IGeneralizedCodec.IsSupportedType(Type type)
    {
        if (type == SelfType) return true;

        if (CommonCodecTypeFilter.IsAbstractOrFrameworkType(type)) return false;

        foreach (var selector in _serializableTypeSelectors)
        {
            if (selector.IsSupportedType(type))
                return true;
        }

        if (_options.IsSerializableType?.Invoke(type) is bool value) return value;

        return IsMemoryPackContract(type, _options.AllowDataContractAttributes);
    }

    /// <inheritdoc />
    object IDeepCopier.DeepCopy(object input, CopyContext context)
    {
        if (context.TryGetCopy(input, out object result)) return result;

        var bufferWriter = new BufferWriterBox<PooledBuffer>(new PooledBuffer());
        try
        {
            MemoryPackSerializer.Serialize(input.GetType(), bufferWriter, input);

            var sequence = bufferWriter.Value.AsReadOnlySequence();
            result = MemoryPackSerializer.Deserialize(input.GetType(), sequence);
        }
        finally
        {
            bufferWriter.Value.Dispose();
        }

        context.RecordCopy(input, result);
        return result;
    }

    /// <inheritdoc />
    bool IGeneralizedCopier.IsSupportedType(Type type)
    {
        if (CommonCodecTypeFilter.IsAbstractOrFrameworkType(type)) return false;

        foreach (var selector in _copyableTypeSelectors)
        {
            if (selector.IsSupportedType(type))
                return true;
        }

        if (_options.IsCopyableType?.Invoke(type) is bool value) return value;

        return IsMemoryPackContract(type, _options.AllowDataContractAttributes);
    }

    /// <inheritdoc />
    bool? ITypeFilter.IsTypeAllowed(Type type) =>
        ((IGeneralizedCopier)this).IsSupportedType(type) || ((IGeneralizedCodec)this).IsSupportedType(type) ? true : null;

    private static bool IsMemoryPackContract(Type type, bool allowDataContractAttribute)
    {
        if (SupportedTypes.TryGetValue(type, out var isMemoryPackContract)) return isMemoryPackContract;

        isMemoryPackContract = type.GetCustomAttribute<MemoryPackableAttribute>() is not null;

        if (!isMemoryPackContract && allowDataContractAttribute)
            isMemoryPackContract = type.GetCustomAttribute<DataContractAttribute>() is DataContractAttribute;

        SupportedTypes.TryAdd(type, isMemoryPackContract);
        return isMemoryPackContract;
    }

    private static void ThrowTypeFieldMissing() => throw new RequiredFieldMissingException("Serialized value is missing its type field.");
}