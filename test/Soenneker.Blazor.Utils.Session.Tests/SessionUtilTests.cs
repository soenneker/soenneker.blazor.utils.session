using Soenneker.Blazor.Utils.Session.Abstract;
using Soenneker.Tests.FixturedUnit;
using Xunit;


namespace Soenneker.Blazor.Utils.Session.Tests;

[Collection("Collection")]
public class SessionUtilTests : FixturedUnitTest
{
    private readonly ISessionUtil _util;

    public SessionUtilTests(Fixture fixture, ITestOutputHelper output) : base(fixture, output)
    {
        _util = Resolve<ISessionUtil>(true);
    }

    [Fact]
    public void Default()
    {

    }
}
