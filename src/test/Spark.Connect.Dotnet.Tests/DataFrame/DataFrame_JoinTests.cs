using Spark.Connect.Dotnet.Sql;
using Xunit.Abstractions;

namespace Spark.Connect.Dotnet.Tests.DataFrame;

public class DataFrame_JoinTests : E2ETestBase
{
    public DataFrame_JoinTests(ITestOutputHelper logger) : base(logger)
    {
    }

    [Fact]
    public void CrossJoin_Test()
    {
        var df = Spark.Sql("SELECT id-100 as Col1, id as Col2 from range(100)");
        df.CrossJoin(df).Show();
    }

    [Fact]
    public void Join_Test()
    {
        var df = Spark.Sql("SELECT * from range(100)");

        df.Join(df, new List<string>(), JoinType.Cross).Show();
        df.Join(df, new List<string> { "id" }, JoinType.LeftAnti).Show();
        df.Join(df, new List<string> { "id" }, JoinType.LeftOuter).Show();
        df.Join(df, new List<string> { "id" }, JoinType.LeftSemi).Show();
        df.Join(df, new List<string> { "id" }).Show();
        df.Join(df, new List<string> { "id" }, JoinType.FullOuter).Show();
        df.Join(df, new List<string> { "id" }, JoinType.RightOuter).Show();


        df.Join(df, new List<string>(), JoinType.Cross).Collect();
        df.Join(df, new List<string> { "id" }, JoinType.LeftAnti).Collect();
        df.Join(df, new List<string> { "id" }, JoinType.LeftOuter).Collect();
        df.Join(df, new List<string> { "id" }, JoinType.LeftSemi).Collect();
        df.Join(df, new List<string> { "id" }).Collect();
        df.Join(df, new List<string> { "id" }, JoinType.FullOuter).Collect();
        df.Join(df, new List<string> { "id" }, JoinType.RightOuter).Collect();
    }
}