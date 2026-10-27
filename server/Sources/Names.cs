using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Orleans;
using Turbo.Primitives.Furniture.Providers;
using Turbo.Primitives.Orleans;
using Turbo.Primitives.Players;

namespace TurboDiscord.Sources;

/// <summary>Names of what events name by id (players, furni), and where the admin panel shows them.</summary>
public interface INames
{
    /// <summary>Each player's name; a player the hotel does not know is <c>#id</c>.</summary>
    Task<IReadOnlyDictionary<int, string>> PlayersAsync(IEnumerable<int> ids, CancellationToken ct);

    /// <summary>A furni definition's name; one the hotel does not know is <c>furni #id</c>.</summary>
    string Furni(int definitionId);

    /// <summary>A page of the admin panel (<c>players/12</c>); null when the host has no panel URL.</summary>
    string? Panel(string path);
}

/// <summary>The host's names: the player directory and the furniture definitions.</summary>
public sealed class HostNames(
    IGrainFactory grains,
    IFurnitureDefinitionProvider furniture,
    IConfiguration configuration
) : INames
{
    public async Task<IReadOnlyDictionary<int, string>> PlayersAsync(
        IEnumerable<int> ids,
        CancellationToken ct
    )
    {
        var wanted = ids.Where(x => x > 0).Distinct().ToList();

        if (wanted.Count == 0)
            return new Dictionary<int, string>();

        ImmutableDictionary<PlayerId, string> names;

        try
        {
            names = await grains
                .GetPlayerDirectoryGrain()
                .GetPlayerNamesAsync([.. wanted.Select(x => PlayerId.Parse(x))], ct)
                .ConfigureAwait(false);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            names = ImmutableDictionary<PlayerId, string>.Empty;
        }

        return wanted.ToDictionary(
            x => x,
            x =>
                names.TryGetValue(PlayerId.Parse(x), out var name) && !string.IsNullOrEmpty(name)
                    ? name
                    : $"#{x}"
        );
    }

    public string Furni(int definitionId) =>
        furniture.TryGetDefinition(definitionId)?.Name is { Length: > 0 } name
            ? name
            : $"furni #{definitionId}";

    public string? Panel(string path) =>
        configuration["Turbo:Admin:PanelUrl"]?.TrimEnd('/') is { Length: > 0 } panel
            ? $"{panel}/{path}"
            : null;
}
