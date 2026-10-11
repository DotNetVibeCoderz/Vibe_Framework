using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.SignalR;

namespace ApiPlayground.Server;

/// <summary>
/// The SignalR hub. Each method shows one part of the hub protocol:
/// broadcasting to a group, returning a result, streaming, and failing.
/// </summary>
public sealed class ChatHub : Hub
{
    private static readonly ConcurrentDictionary<string, (string Name, string Room)> People = new();

    /// <summary>Joins a room under a name; everyone in it is told.</summary>
    public async Task Join(string room, string name)
    {
        if (People.TryGetValue(Context.ConnectionId, out var was))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, was.Room);
        People[Context.ConnectionId] = (name, room);
        await Groups.AddToGroupAsync(Context.ConnectionId, room);
        await Clients.Group(room).SendAsync("System", $"{name} joined #{room}");
        await Clients.Group(room).SendAsync("Presence", Names(room));
    }

    /// <summary>Says something to the caller's room.</summary>
    public Task Say(string text)
    {
        if (!People.TryGetValue(Context.ConnectionId, out var me)) throw new HubException("Join a room first.");
        return Clients.Group(me.Room).SendAsync("Message", me.Name, text, DateTime.UtcNow);
    }

    /// <summary>An RPC with a result: the client awaits the sum.</summary>
    public int Add(int a, int b) => a + b;

    /// <summary>A server-to-client stream: counts down, one number every 400 ms.</summary>
    public async IAsyncEnumerable<int> Countdown(int from, [EnumeratorCancellation] CancellationToken cancel)
    {
        for (var i = Math.Clamp(from, 1, 50); i >= 0; i--)
        {
            yield return i;
            await Task.Delay(400, cancel);
        }
    }

    /// <summary>Always fails, so the client can show how a hub error arrives.</summary>
    public string Fail() => throw new HubException("The server refused on purpose: this method always fails.");

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (People.TryRemove(Context.ConnectionId, out var me))
        {
            await Clients.Group(me.Room).SendAsync("System", $"{me.Name} left");
            await Clients.Group(me.Room).SendAsync("Presence", Names(me.Room));
        }
        await base.OnDisconnectedAsync(exception);
    }

    private static string[] Names(string room) => People.Values.Where(p => p.Room == room).Select(p => p.Name).OrderBy(n => n).ToArray();
}
