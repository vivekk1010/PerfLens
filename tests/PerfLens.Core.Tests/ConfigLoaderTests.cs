using PerfLens.Core.Configuration;

namespace PerfLens.Core.Tests;

public class ConfigLoaderTests
{
    private const string MinimalYaml = """
        target:
          baseUrl: https://app.test
        pages:
          - path: /home
        """;

    private static string Var(string suffix) => $"PERFLENS_TEST_{suffix}_{Guid.NewGuid():N}";

    [Fact]
    public void FromYaml_ParsesNestedConfig_AndEnumsCaseInsensitive()
    {
        var c = ConfigLoader.FromYaml("""
            name: Shop
            target:
              baseUrl: https://app.test
              headers:
                X-Env: staging
            pages:
              - name: Home
                path: /
                waitUntil: networkidle
                thresholds:
                  lcpMs: 3000
            browser:
              engine: FIREFOX
              cacheMode: warm
              iterations: 3
              headless: false
              cpuThrottlingRate: 2.5
            api:
              enabled: true
              profile:
                mode: open
                ratePerSecond: 25
              endpoints:
                - name: search
                  url: /api/search
            thresholds:
              pageStatistic: p90
              minScore: 80
            """);

        Assert.Equal("Shop", c.Name);
        Assert.Equal("staging", c.Target.Headers["X-Env"]);
        Assert.Equal(WaitUntil.NetworkIdle, c.Pages[0].WaitUntil);
        Assert.Equal(3000, c.Pages[0].Thresholds!.LcpMs);
        Assert.Equal(BrowserEngine.Firefox, c.Browser.Engine);
        Assert.Equal(CacheMode.Warm, c.Browser.CacheMode);
        Assert.Equal(3, c.Browser.Iterations);
        Assert.False(c.Browser.Headless);
        Assert.Equal(2.5, c.Browser.CpuThrottlingRate);
        Assert.Equal(LoadMode.Open, c.Api.Profile.Mode);
        Assert.Equal(25, c.Api.Profile.RatePerSecond);
        Assert.Equal("p90", c.Thresholds.PageStatistic);
        Assert.Equal(80, c.Thresholds.MinScore);
        Assert.Equal(5, new PerfLensConfig().Browser.Iterations);
    }

    [Fact]
    public void FromJson_CaseInsensitive_CommentsTrailingCommas_Enums()
    {
        var c = ConfigLoader.FromJson("""
            {
              // comment
              "Target": { "BASEURL": "https://app.test" },
              "pages": [ { "path": "/a", "waitUntil": "domcontentloaded" }, ],
              "auth": { "type": "HEADERS", "headers": { "Authorization": "Bearer x" } },
            }
            """);

        Assert.Equal("https://app.test", c.Target.BaseUrl);
        Assert.Equal(WaitUntil.DomContentLoaded, c.Pages[0].WaitUntil);
        Assert.Equal(AuthType.Headers, c.Auth.Type);
    }

    [Fact]
    public void Yaml_NumericAndBooleanScalars_IntoStringProperties()
    {
        var c = ConfigLoader.FromYaml("""
            name: 2024
            target:
              baseUrl: https://app.test
              headers:
                X-Tenant: 12345
                X-Flag: true
                X-Ratio: 1.5
            pages:
              - path: /
            """);

        Assert.Equal("2024", c.Name);
        Assert.Equal("12345", c.Target.Headers["X-Tenant"]);
        Assert.Equal("true", c.Target.Headers["X-Flag"]);
        Assert.Equal("1.5", c.Target.Headers["X-Ratio"]);
    }

    [Fact]
    public void Yaml_QuotedNumericStrings_ArePreservedVerbatim()
    {
        var c = ConfigLoader.FromYaml("""
            target:
              baseUrl: https://app.test
              cookies:
                - name: tenant
                  value: "007"
              headers:
                X-Version: "1.10"
            pages:
              - path: /
            """);

        Assert.Equal("007", c.Target.Cookies[0].Value);
        Assert.Equal("1.10", c.Target.Headers["X-Version"]);
    }

    [Fact]
    public void EnvSubstitution_EnvPrefix_Plain_AndDefault()
    {
        var token = Var("TOKEN");
        var host = Var("HOST");
        var unset = Var("UNSET");
        Environment.SetEnvironmentVariable(token, "s3cr\"et");
        Environment.SetEnvironmentVariable(host, "app.test");
        try
        {
            var c = ConfigLoader.FromYaml($$"""
                target:
                  baseUrl: https://${{{host}}}
                auth:
                  type: headers
                  headers:
                    Authorization: Bearer ${ENV:{{token}}}
                pages:
                  - path: /${{{unset}}:-fallback}
                """);

            Assert.Equal("https://app.test", c.Target.BaseUrl);
            Assert.Equal("Bearer s3cr\"et", c.Auth.Headers["Authorization"]);
            Assert.Equal("/fallback", c.Pages[0].Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable(token, null);
            Environment.SetEnvironmentVariable(host, null);
        }
    }

    [Fact]
    public void EnvSubstitution_SetVariableWinsOverDefault_EmptyDefaultAllowed()
    {
        var v = Var("SET");
        Environment.SetEnvironmentVariable(v, "real");
        try
        {
            Assert.Equal("real|", ConfigLoader.SubstituteEnvironment($"${{{v}:-dflt}}|${{{Var("NOPE")}:-}}"));
        }
        finally { Environment.SetEnvironmentVariable(v, null); }
    }

    [Fact]
    public void EnvSubstitution_MissingVariable_ThrowsWithNames()
    {
        var a = Var("A");
        var b = Var("B");
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.FromJson($$"""{ "name": "${ENV:{{a}}} ${{{b}}} ${{{a}}}" }"""));
        Assert.Contains(a, ex.Message);
        Assert.Contains(b, ex.Message);
    }

    [Fact]
    public void EnvSubstitution_JsonUnquotedNumber()
    {
        var v = Var("ITER");
        Environment.SetEnvironmentVariable(v, "7");
        try
        {
            var c = ConfigLoader.FromJson($$"""{ "target": { "baseUrl": "https://app.test" }, "browser": { "iterations": ${{{v}}} } }""");
            Assert.Equal(7, c.Browser.Iterations);
        }
        finally { Environment.SetEnvironmentVariable(v, null); }
    }

    [Fact]
    public void EnvSubstitution_YamlNumericProperty()
    {
        var v = Var("ITER");
        Environment.SetEnvironmentVariable(v, "7");
        try
        {
            var c = ConfigLoader.FromYaml($$"""
                target:
                  baseUrl: https://app.test
                browser:
                  iterations: ${ENV:{{v}}}
                pages:
                  - path: /
                """);
            Assert.Equal(7, c.Browser.Iterations);
        }
        finally { Environment.SetEnvironmentVariable(v, null); }
    }

    [Fact]
    public void InvalidYamlAndJson_ThrowConfigException()
    {
        Assert.Throws<ConfigException>(() => ConfigLoader.FromYaml("target: [unclosed"));
        Assert.Throws<ConfigException>(() => ConfigLoader.FromJson("{ \"target\": "));
        Assert.Throws<ConfigException>(() => ConfigLoader.FromJson("{ \"browser\": { \"iterations\": \"many\" } }"));
        Assert.Throws<ConfigException>(() => ConfigLoader.FromYaml(""));
    }

    [Fact]
    public void Validate_ValidConfig_FillsDefaultNames()
    {
        var c = ConfigLoader.FromYaml("""
            target:
              baseUrl: https://app.test
            pages:
              - path: /home
            api:
              enabled: true
              endpoints:
                - url: /api/x
                  method: post
            """);

        ConfigLoader.Validate(c);

        Assert.Equal("/home", c.Pages[0].Name);
        Assert.Equal("POST /api/x", c.Api.Endpoints[0].Name);
    }

    [Theory]
    [InlineData("pages: [ { path: / } ]", "target.baseUrl is required")]
    [InlineData("target: { baseUrl: ftp://x.test }\npages: [ { path: / } ]", "absolute http(s) URL")]
    [InlineData("target: { baseUrl: https://x.test }", "Nothing to measure")]
    [InlineData("target: { baseUrl: https://x.test }\npages: [ { path: /, iterations: 0 } ]", "pages[0].iterations must be >= 1")]
    [InlineData("target: { baseUrl: https://x.test }\npages: [ { name: A, path: /a }, { name: a, path: /b } ]", "Duplicate page names")]
    [InlineData("target: { baseUrl: https://x.test }\npages: [ { path: / } ]\nbrowser: { iterations: 0 }", "browser.iterations must be >= 1")]
    [InlineData("target: { baseUrl: https://x.test }\npages: [ { path: / } ]\nbrowser: { networkProfile: custom }", "customNetwork is required")]
    [InlineData("target: { baseUrl: https://x.test }\ncrawl: { enabled: true, include: [ '([' ] }", "Invalid crawl regex")]
    [InlineData("target: { baseUrl: https://x.test }\npages: [ { path: / } ]\nauth: { type: login }", "auth.login.steps are required")]
    [InlineData("target: { baseUrl: https://x.test }\npages: [ { path: / } ]\nauth: { type: storagestate }", "auth.storageStatePath is required")]
    [InlineData("target: { baseUrl: https://x.test }\npages: [ { path: / } ]\nauth: { type: headers }", "auth.headers are required")]
    [InlineData("target: { baseUrl: https://x.test }\napi: { enabled: true }", "api.endpoints is empty")]
    [InlineData("target: { baseUrl: https://x.test }\napi: { enabled: true, useDiscoveredEndpoints: true }", "needs pages or crawl")]
    [InlineData("target: { baseUrl: https://x.test }\napi: { enabled: true, endpoints: [ { url: /x, profile: { durationSeconds: 0 } } ] }", "durationSeconds must be >= 1")]
    [InlineData("target: { baseUrl: https://x.test }\npages: [ { path: / } ]\nthresholds: { pageStatistic: p42 }", "thresholds.pageStatistic")]
    [InlineData("target: { baseUrl: https://x.test }\npages: [ { path: / } ]\noutput: { formats: [ html, pdf ] }", "Unknown output format 'pdf'")]
    public void Validate_Errors(string yaml, string expectedMessage)
    {
        var c = ConfigLoader.FromYaml(yaml);
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(c));
        Assert.Contains(expectedMessage, ex.Message);
    }

    [Fact]
    public void Validate_AggregatesMultipleErrors()
    {
        var c = new PerfLensConfig();
        c.Output.Formats.Add("pdf");
        var ex = Assert.Throws<ConfigException>(() => ConfigLoader.Validate(c));
        Assert.Contains("target.baseUrl is required", ex.Message);
        Assert.Contains("Nothing to measure", ex.Message);
        Assert.Contains("pdf", ex.Message);
    }

    [Fact]
    public void Thresholds_PartialGlobalBlockKeepsDefaults_PerPageFieldsStayUnset()
    {
        var c = ConfigLoader.FromYaml("""
            target: { baseUrl: https://x.test }
            pages: [ { path: /, thresholds: { lcpMs: 4000 } } ]
            thresholds:
              page: { ttfbMs: 500, tbtMs: null }
              api: { p95Ms: 300 }
            """);

        Assert.Equal(500, c.Thresholds.Page.TtfbMs);
        Assert.Null(c.Thresholds.Page.TbtMs);
        Assert.Equal(2500, c.Thresholds.Page.LcpMs);
        Assert.Equal(300, c.Thresholds.Api.P95Ms);
        Assert.Equal(1500, c.Thresholds.Api.P99Ms);

        var page = c.Pages[0].Thresholds!;
        Assert.Equal(4000, page.LcpMs);
        Assert.Null(page.TtfbMs);
    }

    [Fact]
    public void Validate_ApiOnly_SkipsBrowserChecks()
    {
        var c = ConfigLoader.FromYaml("""
            target: { baseUrl: https://x.test }
            browser: { iterations: 0 }
            api: { enabled: true, endpoints: [ { url: /x } ] }
            """);
        ConfigLoader.Validate(c);
    }

    [Fact]
    public void Load_FileByExtension_AndMissingFile()
    {
        var dir = Directory.CreateTempSubdirectory("perflens-core-tests");
        try
        {
            var yml = Path.Combine(dir.FullName, "c.yml");
            File.WriteAllText(yml, MinimalYaml);
            Assert.Equal("/home", ConfigLoader.Load(yml).Pages[0].Name);

            var json = Path.Combine(dir.FullName, "c.json");
            File.WriteAllText(json, """{ "target": { "baseUrl": "https://app.test" }, "crawl": { "enabled": true } }""");
            Assert.True(ConfigLoader.Load(json).Crawl.Enabled);

            var bad = Path.Combine(dir.FullName, "bad.yaml");
            File.WriteAllText(bad, "target: { baseUrl: nope }\npages: [ { path: / } ]");
            Assert.Throws<ConfigException>(() => ConfigLoader.Load(bad));

            var missing = Assert.Throws<ConfigException>(() => ConfigLoader.Load(Path.Combine(dir.FullName, "nope.yml")));
            Assert.Contains("not found", missing.Message);
        }
        finally { dir.Delete(true); }
    }
}
