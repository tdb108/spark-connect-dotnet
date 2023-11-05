using Google.Protobuf.Collections;

namespace Spark.Connect.Dotnet.Sql;

public class DataFrameReader
{
    private readonly SparkSession _session;
    private readonly MapField<string, string> _options = new MapField<string, string>();

    private string _schema = String.Empty;

    protected internal DataFrameReader(SparkSession session)
    {
        _session = session;
    }

    /// <summary>
    /// Mockable `DataFrameReader` constructor. If you would like to write unit tests using `DataFrameReader` and would like to mock it then
    ///  you can use this constructor. It is not to be used in the actual code, if you try to then any call is guaranteed to fail.
    /// </summary>
#pragma warning disable CS8618
    protected internal DataFrameReader()
    {
        
    }
#pragma warning restore CS8618

    public DataFrameReader Option(string key, string value)
    {
        _options.Add(key, value);
        return this;
    }
    
    public DataFrameReader Options(List<KeyValuePair<string, string>> options)
    {
        foreach (var option in options)
        {
            _options.Add(option.Key, option.Value);
        }
        return this;
    }
    
    public DataFrameReader Options(Dictionary<string, string> options)
    {
        foreach (var option in options)
        {
            _options.Add(option.Key, option.Value);
        }
        return this;
    }

    public DataFrameReader Schema(string schema)
    {
        _schema = schema;
        return this;
    }
    
    public DataFrameReader Schema(AnalyzePlanRequest.Types.Schema schema)
    {
        throw new NotImplementedException();
    }
    
    public DataFrame Json(string path) => Read(new string[]{path}, "json", _options, _schema);

    public DataFrame Json(params string[] paths) => Read(paths, "json", _options, _schema);
    
    public DataFrame Csv(string path) => Read(new string[]{path}, "csv", _options, _schema);
    
    public DataFrame Csv(params string[] paths) => Read(paths, "csv", _options, _schema);
    
    public DataFrame Orc(string path) => Read(new string[] { path }, "orc", _options, _schema);

    public DataFrame Orc(params string[] paths) => Read(paths, "orc", _options, _schema);
    
    public DataFrame Parquet(string path) => Read(new string[]{path}, "orc", _options, _schema);

    public DataFrame Parquet(params string[] paths) => Read(paths, "orc", _options, _schema);
    
    public DataFrame Read(string[] paths, string format, MapField<string, string> options, string schema)
    {
        var datasource = new Read.Types.DataSource()
        {
            Format = format, Paths = { paths }
        };

        if (options.Count > 0)
        {
            datasource.Options.Add(options);    
        }

        if (!string.IsNullOrEmpty(schema))
        {
            datasource.Schema = schema;
        }
        
        var plan = new Plan()
        {
            Root = new Relation()
            {
                Read = new Read()
                {
                    DataSource = datasource
                }
            }
        };
        return new DataFrame(_session, plan.Root);
    }

    public DataFrame Jdbc(string[] paths, string format, MapField<string, string> options, string schema, RepeatedField<string> predicates)
    {
        var datasource = new Read.Types.DataSource()
        {
            Format = format, Paths = { paths }
        };

        if (options.Count > 0)
        {
            datasource.Options.Add(options);    
        }

        if (!string.IsNullOrEmpty(schema))
        {
            datasource.Schema = schema;
        }

        foreach (var predicate in predicates)
        {
            datasource.Predicates.Add(predicate);
        }

        var plan = new Plan()
        {
            Root = new Relation()
            {
                Read = new Read()
                {
                    DataSource = datasource
                }
            }
        };

        return new DataFrame(_session, plan.Root);
    }
}