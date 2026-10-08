namespace Argon.Features.Env;

/// <summary>
/// What is left of the environment-shape API.
/// </summary>
/// <remarks>
/// This file used to hold <c>ArgonRoleKind { Hybrid, Gateway, EntryPoint, Worker }</c> and
/// <c>ArgonEnvironmentKind { SingleInstance, SingleRegion, MultiRegion }</c>, read out of
/// <c>ARGON_ROLE</c> and <c>ARGON_MODE</c>, plus the dozen <c>IsX()</c> predicates every consumer
/// branched on. All of it is replaced by <see cref="Clustering.RoleDescriptor"/>: a process is told
/// what it is by <c>--role</c>, resolves one declared role, and asks that role rather than guessing
/// from a pair of enums.
/// <para>
/// Kept empty rather than deleted so the namespace survives for anything that still imports it;
/// remove the file once no <c>using Argon.Features.Env;</c> remains.
/// </para>
/// </remarks>
public static class EnvironmentExtensions;
