namespace Argon.Features.Clustering;

using ion.runtime;
using Orleans.Serialization;

/// <summary>
/// Lets Orleans name an Ion union on the wire.
/// </summary>
/// <remarks>
/// <para>Orleans writes the actual type's name into a message whenever the declared type does not pin
/// it down, and every name it writes has to be one the type manifest allows — a gate that exists so a
/// hostile payload cannot ask a process to construct arbitrary types. Ion's generated contracts carry
/// no <c>[GenerateSerializer]</c>, so they are in no manifest, and a grain method declared to return
/// <c>IEnableOTPResult</c> could not answer at all: the call failed while encoding the response, after
/// the grain had already done its work.</para>
///
/// <para><b>The failure was invisible to every test.</b> A grain call inside one process is not
/// serialized, so a co-hosted host answered these methods perfectly and only a deployment with the
/// client role in a different pod from the silo ever saw a <c>500</c>. It reached production on
/// <c>ISecurityGrain</c>, whose every method returns a union.</para>
///
/// <para>Scoped to <see cref="IIonUnion{T}"/> rather than to an assembly or to everything, because
/// that interface marks exactly the types with the problem: a union is the only shape the code
/// generator emits whose declared type is an interface, and therefore the only one whose name Orleans
/// has to write. Widening it to the whole contracts assembly would allow hundreds of types that never
/// needed it, and <c>AllowAllTypes</c> would remove the gate this is asking permission from.</para>
///
/// <para>Answers <c>null</c> — not <c>false</c> — for everything else: a filter that refuses is a
/// filter that can veto types other filters and the manifest itself would have allowed.</para>
/// </remarks>
public sealed class IonUnionTypeFilter : ITypeFilter
{
    public bool? IsTypeAllowed(Type type)
        => type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IIonUnion<>))
            ? true
            : null;
}
