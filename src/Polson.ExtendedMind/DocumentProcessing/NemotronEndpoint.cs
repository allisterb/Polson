namespace Polson.ExtendedMind.DocumentProcessing;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// One service that serves Nemotron 3.5 Super VL over the OpenAI Chat Completions API: NVIDIA's hosted NIM API, or
/// Nebius Token Factory. <see cref="NemotronChat"/> tries them in order.
/// </summary>
/// <remarks>
/// Measured 2026-10-21 against both: the same request shapes (images and video as data URIs, JSON Schema through
/// <c>response_format</c>) work on each. NIM took 64 images in a request and read all of them; Token Factory takes
/// at most 8, so a request is built to fit the smaller.
/// </remarks>
/// <param name="Name"><see cref="Nim"/> or <see cref="Nebius"/>; what <c>provenance.endpoint</c> reports.</param>
/// <param name="BaseUrl">Up to and including <c>/v1</c>.</param>
/// <param name="ApiKey">Sent as a bearer token, never logged.</param>
/// <param name="Model">The model's id on this service. The two name the same model differently.</param>
public sealed record NemotronEndpoint(string Name, string? BaseUrl, string? ApiKey, string Model)
{
    #region Properties
    public const string Nim = "nim";

    public const string Nebius = "nebius";

    public const string NimBaseUrl = "https://integrate.api.nvidia.com/v1";

    public const string NimModel = "nvidia/nemotron-3.5-super-vl-120b-a12b";

    public const string NebiusModel = "nvidia/Nemotron-3.5-Super-VL";

    /// <summary>The preference when none is configured.</summary>
    public const string DefaultPrefer = Nim;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(BaseUrl);
    #endregion

    #region Methods
    /// <summary>The endpoints with <paramref name="prefer"/> first and the rest after, as given; null for a name that is none of them.</summary>
    public static IReadOnlyList<NemotronEndpoint>? InOrder(string? prefer, params NemotronEndpoint[] endpoints)
    {
        var name = string.IsNullOrWhiteSpace(prefer) ? DefaultPrefer : prefer.Trim();
        var first = endpoints.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return first is null ? null : [first, .. endpoints.Where(e => e != first)];
    }
    #endregion
}
