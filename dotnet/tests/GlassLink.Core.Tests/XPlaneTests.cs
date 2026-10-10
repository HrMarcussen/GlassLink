using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GlassLink.Sim;

namespace GlassLink.Core.Tests;

/// <summary>X-Plane's web API, as far as GlassLink uses it: datarefs and commands by name, values, command activation.</summary>
internal sealed class FakeXPlane : HttpMessageHandler
{
    private readonly Dictionary<string, long> _ids = [];
    public readonly Dictionary<string, JsonNode?> Values = [];
    public readonly HashSet<string> Commands = [];
    public readonly List<string> Activated = [];
    public bool AcceptCommands = true;
    public bool AnswerValues = true;

    public FakeXPlane(string aircraftPath)
    {
        Values["sim/aircraft/view/acf_relative_path"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(aircraftPath + "\0\0"));
        Values["sim/aircraft/view/acf_ui_name"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("ToLiSs A321"));
        Values["sim/time/framerate_period"] = 0.02;
    }

    private long Id(string name) => _ids.TryGetValue(name, out var id) ? id : _ids[name] = 1000 + _ids.Count;

    private string NameOf(long id) => _ids.First(kv => kv.Value == id).Key;

    private static HttpResponseMessage Json(JsonNode? node) => new(HttpStatusCode.OK) { Content = new StringContent(node?.ToJsonString() ?? "null") };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = Uri.UnescapeDataString(request.RequestUri!.PathAndQuery);
        HttpResponseMessage answer;
        lock (this)
        {
            if (path.EndsWith("/api/capabilities"))
            {
                answer = Json(JsonNode.Parse("""{"api":{"versions":["v3"]},"x-plane":{"version":"12.4.4"}}"""));
            }
            else if (path.Contains("/datarefs?filter[name]=") && path.Split("=")[1] is var dataref && Values.ContainsKey(dataref))
            {
                answer = Json(new JsonObject { ["data"] = new JsonArray(new JsonObject { ["id"] = Id(dataref), ["name"] = dataref }) });
            }
            else if (path.Contains("/commands?filter[name]=") && path.Split("=")[1] is var command && Commands.Contains(command))
            {
                answer = Json(new JsonObject { ["data"] = new JsonArray(new JsonObject { ["id"] = Id(command), ["name"] = command }) });
            }
            else if (path.EndsWith("/value") && AnswerValues)
            {
                answer = Json(new JsonObject { ["data"] = Values[NameOf(long.Parse(path.Split('/')[^2]))]?.DeepClone() });
            }
            else if (path.EndsWith("/activate") && request.Method == HttpMethod.Post)
            {
                Activated.Add(NameOf(long.Parse(path.Split('/')[^2])));
                answer = new HttpResponseMessage(AcceptCommands ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable);
            }
            else
            {
                answer = new HttpResponseMessage(path.EndsWith("/value") ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound);
            }
        }

        return Task.FromResult(answer);
    }
}

public class XPlaneTests
{
    private const string ToLiss = "Aircraft/ToLissA321_V1p7p2/a321.acf";

    private static (FakeXPlane Fake, XPlaneClient Client, XPlanePopout Popout, Func<bool[]> _, Action Tick, List<string> Missing, Action<bool> SetAnyPopout) Make(
        Func<bool>? enabled = null)
    {
        var fake = new FakeXPlane(ToLiss);
        foreach (var d in XPlaneProfiles.ToLiss.Displays.Values)
        {
            fake.Commands.Add(d.Command);
        }

        fake.Commands.Add(XPlaneProfiles.ToLiss.ReinstateCommand!);
        fake.Values[XPlaneProfiles.ToLiss.StateArray!] = new JsonArray(0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var client = new XPlaneClient(handler: fake, running: () => true);
        client.Poll();
        var missing = new List<string> { "pfd", "nd" };
        long now = 0;
        var any = false;
        var popout = new XPlanePopout(client, () => missing.ToList(), _ => { }, () => now, _ => any, timer: false, enabled: enabled);
        popout.Tick();                                                // the first look starts the clock: pop-outs get 5 s to appear
        Assert.Empty(fake.Activated);
        return (fake, client, popout, () => [], () => { now += 10_000; popout.Tick(); }, missing, v => any = v);
    }

    [Fact]
    public void The_client_finds_x_plane_and_the_aircraft_through_the_web_api()
    {
        var (fake, client, _, _, _, _, _) = Make();
        Assert.True(client.Connected);
        Assert.Equal(ToLiss, client.AircraftPath);                    // base64 "data" dataref, zero bytes cut off
        Assert.Equal("ToLiSs A321", client.AircraftName);
        Assert.Equal(50, client.Fps);
        Assert.True(client.Command("AirbusFBW/PopUpPFD1"));
        Assert.Equal(["AirbusFBW/PopUpPFD1"], fake.Activated);
        Assert.False(client.Command("no/such/command"));
    }

    [Fact]
    public void After_loading_the_last_flights_pop_outs_are_reinstated_once_and_again_if_x_plane_did_not_take_it()
    {
        var (fake, _, popout, _, tick, _, _) = Make();
        fake.AcceptCommands = false;                                  // X-Plane still busy loading
        tick();
        Assert.Equal(["toliss_airbus/reinstatePopups"], fake.Activated);
        fake.AcceptCommands = true;
        tick();
        Assert.Equal(2, fake.Activated.Count(c => c == "toliss_airbus/reinstatePopups"));   // sent again: the first one was lost
        tick();
        Assert.Equal(2, fake.Activated.Count(c => c == "toliss_airbus/reinstatePopups"));   // taken: not a third time
        Assert.Equal("running", popout.State.Status);
    }

    [Fact]
    public void Switched_off_it_opens_nothing_until_asked()
    {
        var (fake, _, popout, _, tick, _, _) = Make(() => false);
        tick();
        tick();
        Assert.Empty(fake.Activated);
        Assert.Equal("off", popout.State.Status);
        popout.Retry();                                               // "Pop out missing displays now"
        tick();                                                       // the first look starts the clock
        tick();
        Assert.Equal(["toliss_airbus/reinstatePopups"], fake.Activated);
    }

    [Fact]
    public void Without_the_popup_states_nothing_is_toggled()
    {
        var (fake, _, popout, _, tick, _, setAny) = Make();
        setAny(true);                                                 // some pop-outs are open: no reinstate
        fake.AnswerValues = false;                                    // X-Plane too busy to say which popups are open
        tick();
        Assert.Empty(fake.Activated);                                 // a toggle could close a popup that is open
        Assert.Contains("busy", popout.State.Detail);
    }

    [Fact]
    public void Only_commands_x_plane_took_count_and_three_without_a_window_give_up()
    {
        var (fake, _, popout, _, tick, _, setAny) = Make();
        setAny(true);
        fake.AcceptCommands = false;
        for (var i = 0; i < 5; i++)
        {
            tick();
        }

        Assert.NotEqual("gave_up", popout.State.Status);             // refused commands are no tries
        fake.AcceptCommands = true;
        for (var i = 0; i < 4; i++)
        {
            tick();
        }

        Assert.Equal("gave_up", popout.State.Status);
        Assert.Equal(3, fake.Activated.Count(c => c == "AirbusFBW/PopUpPFD1") - 5);
        popout.Retry("pfd");
        tick();
        Assert.Equal(4, fake.Activated.Count(c => c == "AirbusFBW/PopUpPFD1") - 5);
    }

    [Fact]
    public void An_open_popup_without_a_window_is_reported_as_inside_x_plane()
    {
        var (fake, _, popout, _, tick, _, setAny) = Make();
        setAny(true);
        tick();                                                       // the PFD popup is opened
        fake.Values[XPlaneProfiles.ToLiss.StateArray!] = new JsonArray(0, 0, 1, 0, 1, 0, 0, 0, 0, 0);    // open, still no window
        tick();
        Assert.Equal("waiting", popout.State.Status);
        Assert.Contains("inside X-Plane", popout.State.Detail);
    }
}
