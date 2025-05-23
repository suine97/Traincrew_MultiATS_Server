using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CsvHelper;
using CsvHelper.Configuration;
using Microsoft.EntityFrameworkCore;
using Traincrew_MultiATS_Server.Data;
using Traincrew_MultiATS_Server.Models;
using Traincrew_MultiATS_Server.Repositories.Datetime;
using Traincrew_MultiATS_Server.Scheduler;
using Route = Traincrew_MultiATS_Server.Models.Route;

namespace Traincrew_MultiATS_Server.HostedService;

public class InitDbHostedService(
    IServiceScopeFactory serviceScopeFactory,
    ILoggerFactory loggerFactory
    ) : IHostedService
{
    private readonly List<Scheduler.Scheduler> _schedulers = [];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = serviceScopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var datetimeRepository = scope.ServiceProvider.GetRequiredService<IDateTimeRepository>();
        var dbInitializer = await CreateDBInitializer(context, datetimeRepository, cancellationToken);
        if (dbInitializer != null)
        {
            await dbInitializer.Initialize();
        }

        await InitRendoTable(context, datetimeRepository, cancellationToken);
        await InitOperationNotificationDisplay(context, datetimeRepository, cancellationToken);
        await InitRouteCsv(context, cancellationToken);

        if (dbInitializer != null)
        {
            await dbInitializer.InitializePost();
        }

        _schedulers.AddRange([
            new SwitchingMachineScheduler(serviceScopeFactory),
            new RendoScheduler(serviceScopeFactory),
            new OperationNotificationScheduler(serviceScopeFactory),
        ]);
    }

    private async Task<DbInitializer?> CreateDBInitializer(ApplicationDbContext context,
        IDateTimeRepository dateTimeRepository,
        CancellationToken cancellationToken)
    {
        var jsonstring = await File.ReadAllTextAsync("./Data/DBBase.json", cancellationToken);
        var DBBase = JsonSerializer.Deserialize<DBBasejson>(jsonstring);
        var logger = loggerFactory.CreateLogger<DbInitializer>();
        return DBBase != null ? new DbInitializer(DBBase, context, dateTimeRepository, logger, cancellationToken) : null;
    }

    private async Task InitRendoTable(
        ApplicationDbContext context,
        IDateTimeRepository dateTimeRepository,
        CancellationToken cancellationToken)
    {
        var rendoTableDir = new DirectoryInfo("./Data/RendoTable");
        if (!rendoTableDir.Exists)
        {
            return;
        }

        var logger = loggerFactory.CreateLogger<DbRendoTableInitializer>();

        List<DbRendoTableInitializer> initializers = [];
        foreach (var file in rendoTableDir.EnumerateFiles())
        {
            if (file.Extension != ".csv")
            {
                continue;
            }

            var stationId = file.Name.Replace(".csv", "");
            var config = new CsvConfiguration(CultureInfo.InvariantCulture)
            {
                HasHeaderRecord = false,
            };
            using var reader = new StreamReader(file.FullName);
            // ヘッダー行を読み飛ばす
            await reader.ReadLineAsync(cancellationToken);
            using var csv = new CsvReader(reader, config);
            var records = await csv
                .GetRecordsAsync<RendoTableCSV>(cancellationToken)
                .ToListAsync(cancellationToken);
            var initializer =
                new DbRendoTableInitializer(stationId, records, context, dateTimeRepository, logger, cancellationToken);
            initializers.Add(initializer);
            await initializer.InitializeObjects();
        }

        var changedEntriesCopy = context.ChangeTracker.Entries()
            .Where(e => e.State is
                EntityState.Added or EntityState.Modified or EntityState.Deleted or EntityState.Unchanged)
            .ToList();

        foreach (var entry in changedEntriesCopy)
        {
            entry.State = EntityState.Detached;
        }

        foreach (var initializer in initializers)
        {
            await initializer.InitializeLocks();
        }
    }

    private async Task InitOperationNotificationDisplay(
        ApplicationDbContext context,
        IDateTimeRepository dateTimeRepository,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo("./Data/運転告知器.csv");
        if (!file.Exists)
        {
            return;
        }

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
        };
        using var reader = new StreamReader(file.FullName);
        // ヘッダー行を読み飛ばす
        await reader.ReadLineAsync(cancellationToken);
        using var csv = new CsvReader(reader, config);
        csv.Context.RegisterClassMap<OperationNotificationDisplayCsvMap>();
        var records = await csv
            .GetRecordsAsync<OperationNotificationDisplayCsv>(cancellationToken)
            .ToListAsync(cancellationToken);
        var trackCircuitNames = records
            .SelectMany(r => r.TrackCircuitNames)
            .ToList();
        var trackCircuits = await context.TrackCircuits
            .Where(tc => trackCircuitNames.Contains(tc.Name))
            .ToDictionaryAsync(tc => tc.Name, cancellationToken);
        var operationNotificationDisplayNames = await context.OperationNotificationDisplays
            .Select(ond => ond.Name)
            .ToListAsync(cancellationToken);
        List<TrackCircuit> changedTrackCircuits = [];
        foreach (var record in records)
        {
            var name = record.Name;
            if (operationNotificationDisplayNames.Contains(name))
            {
                continue;
            }

            context.OperationNotificationDisplays.Add(new()
            {
                Name = name,
                StationId = record.StationId,
                IsUp = record.IsUp,
                IsDown = record.IsDown,
                OperationNotificationState = new()
                {
                    DisplayName = name,
                    Type = OperationNotificationType.None,
                    Content = "",
                    OperatedAt = dateTimeRepository.GetNow().AddDays(-1)
                }
            });
            foreach (var trackCircuitName in record.TrackCircuitNames)
            {
                if (!trackCircuits.TryGetValue(trackCircuitName, out var trackCircuit))
                {
                    continue;
                }

                trackCircuit.OperationNotificationDisplayName = name;
                context.TrackCircuits.Update(trackCircuit);
                changedTrackCircuits.Add(trackCircuit);
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        
        foreach (var trackCircuit in changedTrackCircuits)
        {
            context.Entry(trackCircuit).State = EntityState.Detached;
        }
    }

    private async Task InitRouteCsv(
        ApplicationDbContext context,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo("./Data/進路.csv");
        if (!file.Exists)
        {
            return;
        }

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
        };
        using var reader = new StreamReader(file.FullName);
        // ヘッダー行を読み飛ばす
        await reader.ReadLineAsync(cancellationToken);
        using var csv = new CsvReader(reader, config);
        csv.Context.RegisterClassMap<RouteLockTrackCircuitCsvMap>();
        var records = await csv
            .GetRecordsAsync<RouteLockTrackCircuitCsv>(cancellationToken)
            .ToListAsync(cancellationToken);
        var routes = await context.Routes
            .Select(r => new { r.Name, r.Id })
            .ToDictionaryAsync(r => r.Name, r => r.Id, cancellationToken);
        var trackCircuits = await context.TrackCircuits
            .Select(tc => new { tc.Name, tc.Id })
            .ToDictionaryAsync(tc => tc.Name, tc => tc.Id, cancellationToken);
        var routeLockTrackCircuits = (await context.RouteLockTrackCircuits
            .Select(r => new { r.RouteId, r.TrackCircuitId })
            .ToListAsync(cancellationToken)).ToHashSet();
        foreach (var record in records)
        {
            // 該当進路が登録されていない場合スキップ
            if (!routes.TryGetValue(record.RouteName, out var routeId))
            {
                continue;
            }

            foreach (var trackCircuitName in record.TrackCircuitNames)
            {
                // 該当軌道回路が登録されていない場合スキップ
                if (!trackCircuits.TryGetValue(trackCircuitName, out var trackCircuitId))
                {
                    continue;
                }

                // 既に登録済みの場合、スキップ
                if (routeLockTrackCircuits.Contains(new { RouteId = routeId, TrackCircuitId = trackCircuitId }))
                {
                    continue;
                }

                context.RouteLockTrackCircuits.Add(new()
                {
                    RouteId = routeId,
                    TrackCircuitId = trackCircuitId
                });
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_schedulers.Select(s => s.Stop()));
    }
}

internal partial class DbInitializer(
    DBBasejson DBBase,
    ApplicationDbContext context,
    IDateTimeRepository dateTimeRepository,
    ILogger<DbInitializer> logger,
        CancellationToken cancellationToken)
{
    [GeneratedRegex(@"^(TH(\d{1,2}S?))_")]
    private static partial Regex RegexStationId();

    internal async Task Initialize()
    {
        await InitStation();
        await InitStationTimerState();
        await InitTrackCircuit();
        await InitSignalType();
        await InitSignal();
        await InitNextSignal();
        await InitTrackCircuitSignal();
    }

    internal async Task InitializePost()
    {
        await InitializeSignalRoute();
        await InitializeThrowOutControl();
        await SetStationIdToInterlockingObject();
    }

    private async Task InitStation()
    {
        var stationNames = (await context.Stations
            .Select(s => s.Name)
            .ToListAsync(cancellationToken)).ToHashSet();
        foreach (var station in DBBase.stationList)
        {
            if (stationNames.Contains(station.Name))
            {
                continue;
            }

            context.Stations.Add(new()
            {
                Id = station.Id,
                Name = station.Name,
                IsStation = station.IsStation,
                IsPassengerStation = station.IsPassengerStation
            });
        }
    }

    private async Task InitStationTimerState()
    {
        var stationIds = (await context.Stations
            .Where(s => s.IsStation)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken)).ToHashSet();
        var stationTimerStates = (await context.StationTimerStates
            .Select(s => new { s.StationId, s.Seconds })
            .ToListAsync(cancellationToken)).ToHashSet();
        foreach (var stationId in stationIds)
        {
            foreach (var seconds in new[] { 30, 60 })
            {
                if (stationTimerStates.Contains(new { StationId = stationId, Seconds = seconds }))
                {
                    continue;
                }

                context.StationTimerStates.Add(new()
                {
                    StationId = stationId,
                    Seconds = seconds,
                    IsTeuRelayRaised = RaiseDrop.Drop,
                    IsTenRelayRaised = RaiseDrop.Drop,
                    IsTerRelayRaised = RaiseDrop.Raise,
                });
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task InitTrackCircuit()
    {
        // 全軌道回路情報を取得
        var trackCircuitNames = (await context.TrackCircuits
            .Select(tc => tc.Name)
            .ToListAsync(cancellationToken)).ToHashSet();

        foreach (var item in DBBase.trackCircuitList)
        {
            if (trackCircuitNames.Contains(item.Name))
            {
                continue;
            }

            context.TrackCircuits.Add(new()
            {
                // Todo: ProtectionZoneの未定義部分がなくなったら、ProtectionZoneのデフォルト値の設定を解除
                ProtectionZone = item.ProtectionZone ?? 99,
                Name = item.Name,
                Type = ObjectType.TrackCircuit,
                TrackCircuitState = new()
                {
                    IsShortCircuit = false,
                    IsLocked = false,
                    TrainNumber = "",
                    IsCorrectionDropRelayRaised = RaiseDrop.Drop,
                    IsCorrectionRaiseRelayRaised = RaiseDrop.Drop,
                    DroppedAt = null,
                    RaisedAt = null,
                }
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task InitSignal()
    {
        // 軌道回路情報を取得
        var trackCircuits = await context.TrackCircuits
            .Select(tc => new { tc.Id, tc.Name })
            .ToDictionaryAsync(tc => tc.Name, tc => tc.Id, cancellationToken);
        // 既に登録済みの信号情報を取得
        var signalNames = (await context.Signals
            .Select(s => s.Name)
            .ToListAsync(cancellationToken)).ToHashSet();
        // 駅マスタを取得
        var stations = await context.Stations
            .ToListAsync(cancellationToken);
        // 信号情報登録
        foreach (var signalData in DBBase.signalDataList)
        {
            // 既に登録済みの場合、スキップ
            if (signalNames.Contains(signalData.Name))
            {
                continue;
            }

            ulong trackCircuitId = 0;
            if (signalData.Name.StartsWith("上り閉塞") || signalData.Name.StartsWith("下り閉塞"))
            {
                var trackCircuitName = $"{signalData.Name.Replace("閉塞", "")}T";
                trackCircuits.TryGetValue(trackCircuitName, out trackCircuitId);
            }

            var stationId = stations
                .Where(s => signalData.Name.StartsWith(s.Name))
                .Select(s => s.Id)
                .FirstOrDefault();

            context.Signals.Add(new()
            {
                Name = signalData.Name,
                StationId = stationId,
                TrackCircuitId = trackCircuitId > 0 ? trackCircuitId : null,
                TypeName = signalData.TypeName,
                SignalState = new()
                {
                    IsLighted = true,
                }
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task InitSignalType()
    {
        var signalTypeNames = (await context.SignalTypes
            .Select(st => st.Name)
            .ToListAsync(cancellationToken)).ToHashSet();
        foreach (var signalTypeData in DBBase.signalTypeList)
        {
            if (signalTypeNames.Contains(signalTypeData.Name))
            {
                continue;
            }

            context.SignalTypes.Add(new()
            {
                Name = signalTypeData.Name,
                RIndication = GetSignalIndication(signalTypeData.RIndication),
                YYIndication = GetSignalIndication(signalTypeData.YYIndication),
                YIndication = GetSignalIndication(signalTypeData.YIndication),
                YGIndication = GetSignalIndication(signalTypeData.YGIndication),
                GIndication = GetSignalIndication(signalTypeData.GIndication)
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private static SignalIndication GetSignalIndication(string indication)
    {
        return indication switch
        {
            "R" => SignalIndication.R,
            "YY" => SignalIndication.YY,
            "Y" => SignalIndication.Y,
            "YG" => SignalIndication.YG,
            "G" => SignalIndication.G,
            _ => SignalIndication.R
        };
    }

    private async Task InitNextSignal()
    {
        const int maxDepth = 4;
        foreach (var signalData in DBBase.signalDataList)
        {
            var nextSignalNames = signalData.NextSignalNames ?? [];
            foreach (var nextSignalName in nextSignalNames)
            {
                // Todo: ここでN+1問題が発生しているので、改善したほうが良いかも
                // 既に登録済みの場合、スキップ
                if (context.NextSignals.Any(ns =>
                        ns.SignalName == signalData.Name && ns.TargetSignalName == nextSignalName))
                {
                    continue;
                }

                context.NextSignals.Add(new()
                {
                    SignalName = signalData.Name,
                    SourceSignalName = signalData.Name,
                    TargetSignalName = nextSignalName,
                    Depth = 1
                });
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        var allSignals = await context.Signals.ToListAsync(cancellationToken);
        var nextSignalList = await context.NextSignals
            .Where(ns => ns.Depth == 1)
            .GroupBy(ns => ns.SignalName)
            .ToListAsync(cancellationToken);
        var nextSignalDict = nextSignalList
            .ToDictionary(
                g => g.Key,
                g => g.Select(ns => ns.TargetSignalName).ToList()
            );
        // Todo: このロジック、絶対テスト書いたほうがいい(若干複雑な処理をしてしまったので)
        for (var depth = 2; depth <= maxDepth; depth++)
        {
            var nextNextSignalDict = await context.NextSignals
                .Where(ns => ns.Depth == depth - 1)
                .GroupBy(ns => ns.SignalName)
                .ToDictionaryAsync(
                    g => g.Key,
                    g => g.Select(ns => ns.TargetSignalName).ToList(),
                    cancellationToken
                );
            List<NextSignal> nextNextSignals = [];
            // 全信号機でループ
            foreach (var signal in allSignals)
            {
                // 次信号機がない場合はスキップ
                if (!nextNextSignalDict.TryGetValue(signal.Name, out var nextSignals))
                {
                    continue;
                }

                foreach (var nextSignal in nextSignals)
                {
                    // 次信号機の次信号機を取ってくる
                    if (!nextSignalDict.TryGetValue(nextSignal, out var nnSignals))
                    {
                        continue;
                    }

                    foreach (var nextNextSignal in nnSignals)
                    {
                        // Todo: N+1問題が発生しているので、改善したほうが良いかも
                        if (context.NextSignals.Any(ns =>
                                ns.SignalName == signal.Name && ns.TargetSignalName == nextNextSignal))
                        {
                            continue;
                        }

                        context.NextSignals.Add(new()
                        {
                            SignalName = signal.Name,
                            SourceSignalName = nextSignal,
                            TargetSignalName = nextNextSignal,
                            Depth = depth
                        });
                        await context.SaveChangesAsync(cancellationToken);
                    }
                }
            }
        }
    }

    private async Task InitTrackCircuitSignal()
    {
        foreach (var trackCircuit in DBBase.trackCircuitList)
        {
            var trackCircuitEntity = await context.TrackCircuits
                .FirstOrDefaultAsync(tc => tc.Name == trackCircuit.Name, cancellationToken);

            if (trackCircuitEntity == null) continue;
            foreach (var signalName in trackCircuit.NextSignalNamesUp ?? [])
            {
                // Todo: ここでN+1問題が発生しているので、改善したほうが良いかも
                if (context.TrackCircuitSignals.Any(tcs =>
                        tcs.TrackCircuitId == trackCircuitEntity.Id && tcs.SignalName == signalName && tcs.IsUp))
                {
                    continue;
                }

                context.TrackCircuitSignals.Add(new()
                {
                    TrackCircuitId = trackCircuitEntity.Id,
                    SignalName = signalName,
                    IsUp = true
                });
            }

            foreach (var signalName in trackCircuit.NextSignalNamesDown ?? [])
            {
                // Todo: ここでN+1問題が発生しているので、改善したほうが良いかも
                if (context.TrackCircuitSignals.Any(tcs =>
                        tcs.TrackCircuitId == trackCircuitEntity.Id && tcs.SignalName == signalName && !tcs.IsUp))
                {
                    continue;
                }

                context.TrackCircuitSignals.Add(new()
                {
                    TrackCircuitId = trackCircuitEntity.Id,
                    SignalName = signalName,
                    IsUp = false
                });
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task InitializeSignalRoute()
    {
        var signalRoutes = await context.SignalRoutes
            .Include(sr => sr.Route)
            .ToListAsync(cancellationToken);
        var routes = await context.Routes
            .ToDictionaryAsync(r => r.Name, cancellationToken);
        foreach (var signal in DBBase.signalDataList)
        {
            foreach (var routeName in signal.RouteNames)
            {
                // Todo: FW 全探索なので改善したほうがいいかも
                if (signalRoutes.Any(sr => sr.SignalName == signal.Name && sr.Route.Name == routeName))
                {
                    continue;
                }

                if (!routes.TryGetValue(routeName, out var route))
                {
                    // Todo: 例外を出す
                    continue;
                }

                context.SignalRoutes.Add(new()
                {
                    SignalName = signal.Name,
                    RouteId = route.Id
                });
            }
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task InitializeThrowOutControl()
    {
        var routesByName = await context.Routes
            .ToDictionaryAsync(r => r.Name, cancellationToken);
        var throwOutControlList = (await context.ThrowOutControls
                .Include(toc => toc.SourceRoute)
                .Include(toc => toc.TargetRoute)
                .Select(toc => new { SourceRouteName = toc.SourceRoute.Name, TargetRouteName = toc.TargetRoute.Name })
                .ToListAsync(cancellationToken))
            .ToHashSet();
        foreach (var throwOutControl in DBBase.throwOutControlList)
        {
            // 既に登録済みの場合、スキップ
            if (throwOutControlList.Contains(
                    new { throwOutControl.SourceRouteName, throwOutControl.TargetRouteName }))
            {
                continue;
            }

            // Todo: 方向てこまで実装したらこのスキップを外す
            if (!string.IsNullOrEmpty(throwOutControl.LeverConditionName))
            {
                continue;
            }

            // 進路名を取得
            if (!routesByName.TryGetValue(throwOutControl.SourceRouteName, out var sourceRoute))
            {
                throw new InvalidOperationException($"進路名が見つかりません: {throwOutControl.SourceRouteName}");
            }

            if (!routesByName.TryGetValue(throwOutControl.TargetRouteName, out var targetRoute))
            {
                throw new InvalidOperationException($"進路名が見つかりません: {throwOutControl.TargetRouteName}");
            }
            // Todo: てこの条件をパースする

            context.ThrowOutControls.Add(new()
            {
                SourceRouteId = sourceRoute.Id,
                TargetRouteId = targetRoute.Id
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task SetStationIdToInterlockingObject()
    {
        var interlockingObjects = await context.InterlockingObjects
            .ToListAsync();
        foreach (var interlockingObject in interlockingObjects)
        {
            var match = RegexStationId().Match(interlockingObject.Name);
            if (!match.Success)
            {
                continue;
            }

            var stationId = match.Groups[1].Value;
            interlockingObject.StationId = stationId;
            context.Update(interlockingObject);
        }

        await context.SaveChangesAsync();
    }
}

public partial class DbRendoTableInitializer
{
    const string NameSwitchingMachine = "転てつ器";
    private const string PrefixTrackCircuitDown = "下り";
    private const string PrefixTrackCircuitUp = "上り";
    private const string NameAnd = "and";
    private const string NameOr = "or";
    private const string NameNot = "not";

    private static readonly Dictionary<string, List<string>> StationIdMap = new()
    {
        // 大道寺: 江ノ原検車区、藤江
        { "TH65", ["TH66S", "TH64"] },
        // 江ノ原検車区: 大道寺
        { "TH66S", ["TH65"] },
        // 新野崎: 
        { "TH67", [] },
        // 浜園: 津崎
        { "TH70", ["TH71"] },
        // 津崎: 浜園
        { "TH71", ["TH70"] },
        // 駒野: 館浜
        { "TH75", ["TH76"] },
        // 館浜: 駒野
        { "TH76", ["TH75"] },
    };

    [GeneratedRegex(@"\d+")]
    private static partial Regex RegexIntParse();

    // てこ名を抽出するための正規表現
    [GeneratedRegex(@"(\d+)(?:R|L)(Z?)")]
    private static partial Regex RegexLeverParse();

    // 閉塞軌道回路名を抽出するための正規表現
    [GeneratedRegex(@"^(\d+)T$")]
    private static partial Regex RegexClosureTrackCircuitParse();

    // 信号制御欄から統括制御とそれ以外の部位に分けるための正規表現
    [GeneratedRegex(@"^(.*?)(?:\(\(([^\)\s]+)\)\)\s*)*$")]
    private static partial Regex RegexSignalControl();

    // 連動図表の鎖錠欄の諸々のトークンを抽出するための正規表現
    [GeneratedRegex(@"\[\[|\]\]|\(\(|\)\)|\[|\]|\{|\}|\(|\)|但\s+\d+秒|但|又は|[A-Z\dｲﾛ]+")]
    private static partial Regex TokenRegex();

    // ReSharper disable InconsistentNaming
    private readonly string stationId;
    private readonly List<RendoTableCSV> rendoTableCsvs;
    private readonly ApplicationDbContext context;
    private readonly IDateTimeRepository dateTimeRepository;
    private readonly CancellationToken cancellationToken;
    private readonly List<string> otherStations;
    private readonly ILogger<DbRendoTableInitializer> logger;
    // ReSharper restore InconsistentNaming

    public DbRendoTableInitializer(
        string stationId,
        List<RendoTableCSV> rendoTableCsvs,
        ApplicationDbContext context,
        IDateTimeRepository dateTimeRepository,
        ILogger<DbRendoTableInitializer> logger,
        CancellationToken cancellationToken)
    {
        this.stationId = stationId;
        this.rendoTableCsvs = rendoTableCsvs;
        this.context = context;
        this.dateTimeRepository = dateTimeRepository;
        this.logger = logger;
        this.cancellationToken = cancellationToken;
        otherStations = StationIdMap.GetValueOrDefault(stationId) ?? [];
    }

    internal async Task InitializeObjects()
    {
        PreprocessCsv();
        await InitLever();
        await InitDestinationButtons();
        await InitRoutes();
    }

    internal async Task InitializeLocks()
    {
        // PreprocessCsv();
        await InitLocks();
    }

    private void PreprocessCsv()
    {
        // ヒューマンリーダブルな空白や同上などの項目を補完し、後続の処理で扱いやすくする
        var oldName = "";
        var previousStart = "";
        var preivousLockTime = "";
        var previousApproachLock = "";
        foreach (var rendoTableCsv in rendoTableCsvs)
        {
            if (string.IsNullOrWhiteSpace(rendoTableCsv.Name) || rendoTableCsv.Name.StartsWith('同'))
            {
                rendoTableCsv.Name = oldName;
            }
            else
            {
                oldName = rendoTableCsv.Name;
            }

            if (string.IsNullOrWhiteSpace(rendoTableCsv.Start))
            {
                rendoTableCsv.Start = previousStart;
            }

            // てこ番が違う場合
            if (previousStart != rendoTableCsv.Start)
            {
                preivousLockTime = rendoTableCsv.ApproachTime;
                previousApproachLock = rendoTableCsv.ApproachLock;
            }
            else
            {
                rendoTableCsv.ApproachTime = preivousLockTime;
                rendoTableCsv.ApproachLock = previousApproachLock;
            }

            previousStart = rendoTableCsv.Start;
        }
    }

    private async Task InitLever()
    {
        // 該当駅の全てのてこを取得
        var leverNames = (await context.Levers
            .Where(l => l.Name.StartsWith(stationId))
            .Select(l => l.Name)
            .ToListAsync(cancellationToken)).ToHashSet();
        foreach (var rendoTableCsv in rendoTableCsvs)
        {
            LeverType leverType;
            if (rendoTableCsv.Name.EndsWith("信号機"))
            {
                leverType = LeverType.Route;
            }
            else if (rendoTableCsv.Name.StartsWith(NameSwitchingMachine))
            {
                leverType = LeverType.SwitchingMachine;
            }
            else
            {
                continue;
            }

            // 転てつ器の場合、転てつ器を登録
            SwitchingMachine? switchingMachine = null;
            if (leverType == LeverType.SwitchingMachine)
            {
                switchingMachine = new()
                {
                    Name = CalcSwitchingMachineName(rendoTableCsv.Start, stationId),
                    TcName = "",
                    Type = ObjectType.SwitchingMachine,
                    SwitchingMachineState = new()
                    {
                        IsSwitching = false,
                        IsReverse = NR.Normal,
                        SwitchEndTime = dateTimeRepository.GetNow().AddDays(-1)
                    }
                };
            }

            // てこを登録
            var name = CalcLeverName(rendoTableCsv.Start, stationId);
            if (rendoTableCsv.Start.Length <= 0 || leverNames.Contains(name))
            {
                continue;
            }

            context.Levers.Add(new()
            {
                Name = name,
                Type = ObjectType.Lever,
                LeverType = leverType,
                SwitchingMachine = switchingMachine,
                LeverState = new()
                {
                    IsReversed = LCR.Center
                }
            });
            leverNames.Add(name);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task InitDestinationButtons()
    {
        // 既存の着点ボタン名を一括取得
        var existingButtonNames = await context.DestinationButtons
            .Select(db => db.Name)
            .ToListAsync(cancellationToken);

        foreach (var rendoTableCsv in rendoTableCsvs)
        {
            if (string.IsNullOrWhiteSpace(rendoTableCsv.End) || rendoTableCsv.End is "L" or "R")
            {
                continue;
            }

            // ボタン名を生成
            var buttonName = CalcButtonName(rendoTableCsv.End, stationId);
            if (existingButtonNames.Contains(buttonName))
            {
                continue;
            }

            existingButtonNames.Add(buttonName);

            // 着点ボタンを追加
            context.DestinationButtons.Add(new()
            {
                Name = buttonName,
                StationId = stationId,
                DestinationButtonState = new()
                {
                    IsRaised = RaiseDrop.Drop,
                    OperatedAt = dateTimeRepository.GetNow().AddDays(-1)
                }
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task InitRoutes()
    {
        // 既存の進路名を一括取得
        var existingRouteNames = await context.Routes
            .Select(r => r.Name)
            .Where(r => r.StartsWith(stationId))
            .ToListAsync(cancellationToken);
        var leverDictionary = await context.Levers
            .Where(l => l.Name.StartsWith(stationId))
            .ToDictionaryAsync(l => l.Name, cancellationToken);

        List<(Route, ulong, string)> routes = [];
        foreach (var rendoTableCsv in rendoTableCsvs)
        {
            // RouteTypeを決定
            RouteType routeType;
            if (rendoTableCsv.Name.Contains("場内"))
            {
                routeType = RouteType.Arriving;
            }
            else if (rendoTableCsv.Name.Contains("出発"))
            {
                routeType = RouteType.Departure;
            }
            else if (rendoTableCsv.Name.Contains("誘導"))
            {
                routeType = RouteType.Guide;
            }
            else if (rendoTableCsv.Name.Contains("入換信号"))
            {
                routeType = RouteType.SwitchSignal;
            }
            else if (rendoTableCsv.Name.Contains("入換標識"))
            {
                routeType = RouteType.SwitchRoute;
            }
            else
            {
                continue;
            }

            // 進路名を生成
            var routeName = CalcRouteName(rendoTableCsv.Start, rendoTableCsv.End, stationId);

            if (existingRouteNames.Contains(routeName))
            {
                continue;
            }

            existingRouteNames.Add(routeName);

            // 接近鎖状時素決定
            var matches = RegexIntParse().Match(rendoTableCsv.ApproachTime);
            int? approachLockTime = matches.Success ? int.Parse(matches.Value) : null;

            // 進路を追加
            // Todo: 将来的にTcNameを駅名+進路名にする
            Route route = new()
            {
                Name = routeName,
                TcName = routeName,
                RouteType = routeType,
                RootId = null,
                Indicator = rendoTableCsv.Indicator,
                ApproachLockTime = approachLockTime,
                RouteState = new()
                {
                    IsLeverRelayRaised = RaiseDrop.Drop,
                    IsRouteRelayRaised = RaiseDrop.Drop,
                    IsSignalControlRaised = RaiseDrop.Drop,
                    IsApproachLockMRRaised = RaiseDrop.Drop,
                    IsApproachLockMSRaised = RaiseDrop.Drop,
                    IsRouteLockRaised = RaiseDrop.Drop
                }
            };
            var leverName = CalcLeverName(rendoTableCsv.Start, stationId);
            var buttonName = CalcButtonName(rendoTableCsv.End, stationId);
            if (!leverDictionary.TryGetValue(leverName, out var lever))
            {
                continue;
            }

            routes.Add((route, lever.Id, buttonName));
            context.Routes.Add(route);
        }

        await context.SaveChangesAsync(cancellationToken);

        // 進路とてこと着点ボタンの関連付けを追加
        foreach (var (route, leverId, buttonName) in routes)
        {
            // Todo: 単線区間の進路は、着点がないことに注意
            context.RouteLeverDestinationButtons.Add(new()
            {
                RouteId = route.Id,
                LeverId = leverId,
                DestinationButtonName = buttonName
            });
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task InitLocks()
    {
        // 必要なオブジェクトを取得
        // Todo: 接近鎖錠用に向けたオブジェクト取得
        var interlockingObjects = await context.InterlockingObjects
            .Where(io => io.Name.StartsWith(stationId) || otherStations.Any(s => io.Name.StartsWith(s)))
            .ToListAsync(cancellationToken);
        var locks = (await context.Locks
                .Select(l => l.ObjectId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        // 進路のDict
        var routes = interlockingObjects
            .OfType<Route>()
            .ToList();
        var routesByName = routes
            .ToDictionary(r => r.Name, r => r);
        var routesById = routes
            .ToDictionary(r => r.Id, r => r);
        // 転てつ器のDict
        var switchingMachines = interlockingObjects
            .OfType<SwitchingMachine>()
            .ToDictionary(sm => sm.Name, sm => sm);
        // その他のオブジェクトのDict
        var otherObjects = interlockingObjects
            .Where(io => io is not SwitchingMachine)
            .ToDictionary(io => io.Name, io => io);
        // てこ->進路へのDict
        var leverToRoute = await context.RouteLeverDestinationButtons
            .Join(
                context.Levers,
                rldb => rldb.LeverId,
                l => l.Id,
                (rr, l) => new { l.Name, rr.RouteId }
            )
            .GroupBy(x => x.Name)
            .ToDictionaryAsync(
                g => g.Key,
                g => g.Select(x => x.RouteId).ToList(),
                cancellationToken
            );
        var searchSwitchingMachine = new Func<LockItem, Task<List<InterlockingObject>>>(
            item =>
            {
                // Todo: もっときれいに書けるはず 
                var targetObject =
                    switchingMachines.GetValueOrDefault(CalcSwitchingMachineName(item.Name, item.StationId));
                List<InterlockingObject> result;
                if (targetObject != null)
                {
                    result = [targetObject];
                }
                else
                {
                    result = [];
                }

                return Task.FromResult(result);
            });
        var searchOtherObjects = new Func<LockItem, Task<List<InterlockingObject>>>(item =>
        {
            // 進路(単一) or 軌道回路の場合はこちら
            var key = ConvertHalfWidthToFullWidth(CalcRouteName(item.Name, "", item.StationId));
            var value = otherObjects.GetValueOrDefault(key);
            if (value != null)
            {
                return Task.FromResult<List<InterlockingObject>>([value]);
            }

            // 進路(複数)の場合
            var match = RegexLeverParse().Match(item.Name);
            if (match.Success)
            {
                var leverName = CalcLeverName(match.Groups[1].Value + match.Groups[2].Value, item.StationId);
                var routeIds = leverToRoute.GetValueOrDefault(leverName);
                if (routeIds != null)
                {
                    return Task.FromResult(routeIds.Select(InterlockingObject (r) => routesById[r]).ToList());
                }
            }

            return Task.FromResult<List<InterlockingObject>>([]);
        });
        var searchObjectsForApproachLock = new Func<LockItem, Task<List<InterlockingObject>>>(async item =>
        {
            var result = await searchOtherObjects(item);
            if (result.Count > 0)
            {
                return result;
            }

            // 接近鎖錠の場合、閉塞軌道回路も探す
            var match = RegexClosureTrackCircuitParse().Match(item.Name);
            string trackCircuitName;
            // 閉塞軌道回路
            if (match.Success)
            {
                var trackCircuitNumber = int.Parse(match.Groups[1].Value);
                var prefix = trackCircuitNumber % 2 == 0 ? PrefixTrackCircuitUp : PrefixTrackCircuitDown;
                trackCircuitName = $"{prefix}{trackCircuitNumber}T";
            }
            // 単線の諸々軌道回路
            else
            {
                trackCircuitName = item.Name;
            }

            var trackCircuit = await context.TrackCircuits
                .FirstOrDefaultAsync(tc => tc.Name == trackCircuitName, cancellationToken: cancellationToken);
            if (trackCircuit != null)
            {
                return [trackCircuit];
            }

            return [];
        });

        int? approachLockTime = null;
        foreach (var rendoTableCsv in rendoTableCsvs)
        {
            var routeName = CalcRouteName(rendoTableCsv.Start, rendoTableCsv.End, stationId);
            var route = routesByName.GetValueOrDefault(routeName);
            if (route == null)
            {
                // Todo: Warningを出す
                continue;
            }

            // 既に何らか登録済みの場合、Continue
            if (locks.Contains(route.Id))
            {
                continue;
            }

            // Todo: CTC進路の場合と、その他の進路の場合で処理を分ける
            // 鎖錠欄(転てつ器)
            await RegisterLocks(
                rendoTableCsv.LockToSwitchingMachine, route.Id, searchSwitchingMachine, LockType.Lock, true);
            // 鎖錠欄(そのほか)
            await RegisterLocks(rendoTableCsv.LockToRoute, route.Id, searchOtherObjects, LockType.Lock);

            // 信号制御欄
            var matchSignalControl = RegexSignalControl().Match(rendoTableCsv.SignalControl);
            await RegisterLocks(matchSignalControl.Groups[1].Value, route.Id, searchOtherObjects,
                LockType.SignalControl);
            // 統括制御は、サーバーマスタから読み込む
            // 進路鎖錠
            await RegisterLocks(rendoTableCsv.RouteLock, route.Id, searchOtherObjects,
                LockType.Route, isRouteLock: true);

            // 接近鎖錠
            // Todo: 大道寺13Lの進路は山線用進路なので、一旦スルー(パースするとエラーが出るので)
            if (stationId == "TH65" && rendoTableCsv.Start == "13L")
            {
                continue;
            }

            await RegisterLocks(rendoTableCsv.ApproachLock, route.Id, searchObjectsForApproachLock,
                LockType.Approach);
        }

        // 転てつ器のてっ査鎖錠を処理する
        foreach (var rendoTableCsv in rendoTableCsvs)
        {
            // Todo: 江ノ原61~78転てつ器は、Traincrewが実装していないので、一旦スルーする
            if (stationId == "TH66S" && rendoTableCsv.Start == "61")
            {
                break;
            }
            var targetSwitchingMachines = await searchSwitchingMachine(new()
            {
                Name = rendoTableCsv.Start,
                StationId = stationId
            });
            if (targetSwitchingMachines.Count == 0)
            {
                // Todo: 例外を出す
                continue;
            }

            var switchingMachine = targetSwitchingMachines[0];

            // 既に何らか登録済みの場合、Continue
            if (locks.Contains(switchingMachine.Id))
            {
                continue;
            }

            await RegisterLocks(rendoTableCsv.SignalControl, switchingMachine.Id, searchOtherObjects,
                LockType.Detector);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task RegisterLocks(string lockString, ulong objectId,
        Func<LockItem, Task<List<InterlockingObject>>> searchTargetObjects,
        LockType lockType,
        bool registerSwitchingMachineRoute = false,
        bool isRouteLock = false)
    {
        var lockItems = CalcLockItems(lockString, isRouteLock);

        // ループは基本的に進路鎖状用で、それ以外の場合はループは１回のみ
        for (var i = 0; i < lockItems.Count; i++)
        {
            var lockItem = lockItems[i];
            Lock lockObject = new()
            {
                ObjectId = objectId,
                Type = lockType,
                RouteLockGroup = i + 1,
            };
            context.Locks.Add(lockObject);

            await RegisterLocksInner(
                lockItem, lockObject, null,
                registerSwitchingMachineRoute ? objectId : null, searchTargetObjects);
        }
    }

    private async Task RegisterLocksInner(
        LockItem item,
        Lock lockObject,
        LockCondition? parent,
        ulong? routeIdForSwitchingMachineRoute,
        Func<LockItem, Task<List<InterlockingObject>>> searchTargetObjects)
    {
        LockCondition? current = null;
        if (item.Name == NameOr)
        {
            current = new()
            {
                Lock = lockObject,
                Type = LockConditionType.Or,
                Parent = parent
            };
        }

        if (item.Name == NameAnd)
        {
            current = new()
            {
                Lock = lockObject,
                Type = LockConditionType.And,
                Parent = parent
            };
        }

        if (item.Name == NameNot)
        {
            current = new()
            {
                Lock = lockObject,
                Type = LockConditionType.Not,
                Parent = parent
            };
        }

        // or か and か not の場合、
        if (current != null)
        {
            if (item.Children.Count == 0)
            {
                return;
            }
            context.LockConditions.Add(current);
            foreach (var child in item.Children)
            {
                await RegisterLocksInner(
                    child, lockObject, current, routeIdForSwitchingMachineRoute, searchTargetObjects);
            }

            return;
        }

        // 単一オブジェクトに対する処理
        var targetObjects = await searchTargetObjects(item);
        if (targetObjects.Count == 0)
        {
            if (item.Name.EndsWith('Z'))
            {
                logger.Log(LogLevel.Warning,
                    "誘導進路の進路名が見つかりません。処理をスキップします: {} {}", item.StationId, item.Name);
                return;
            }

            if (item.Name.StartsWith("14") && item.StationId == "TH65")
            {
                // 大道寺14L/Rは、方向てこのためスルー
                logger.Log(LogLevel.Warning, "大道寺14L/Rは方向てこです。処理をスキップします。: {Name}", item.Name);
                return;
            }
            throw new InvalidOperationException($"対象のオブジェクトが見つかりません: {item.StationId} {item.Name}");
        }

        if (targetObjects.Count == 1)
        {
            context.LockConditionObjects.Add(new()
            {
                Lock = lockObject,
                ObjectId = targetObjects[0].Id,
                Parent = parent,
                TimerSeconds = item.TimerSeconds,
                IsReverse = item.IsReverse,
                Type = LockConditionType.Object
            });
            if (routeIdForSwitchingMachineRoute != null && targetObjects[0] is SwitchingMachine switchingMachine)
            {
                context.SwitchingMachineRoutes.Add(new()
                {
                    IsReverse = item.IsReverse,
                    RouteId = routeIdForSwitchingMachineRoute.Value,
                    SwitchingMachineId = switchingMachine.Id,
                });
            }

            return;
        }

        current = new()
        {
            Lock = lockObject,
            Type = LockConditionType.And,
            Parent = parent
        };
        context.LockConditions.Add(current);
        foreach (var targetObject in targetObjects)
        {
            context.LockConditionObjects.Add(new()
            {
                Lock = lockObject,
                Parent = current,
                ObjectId = targetObject.Id,
                TimerSeconds = item.TimerSeconds,
                IsReverse = item.IsReverse,
                Type = LockConditionType.Object
            });
            if (routeIdForSwitchingMachineRoute == null || targetObjects[0] is not SwitchingMachine switchingMachine)
            {
                continue;
            }

            context.SwitchingMachineRoutes.Add(new()
            {
                IsReverse = item.IsReverse,
                RouteId = routeIdForSwitchingMachineRoute.Value,
                SwitchingMachineId = switchingMachine.Id,
            });
        }
    }


    public List<LockItem> CalcLockItems(string lockString, bool isRouteLock)
    {
        var tokens = TokenRegex().Matches(lockString)
            .Select(m => m.Value)
            .ToList();
        var enumerator = tokens.GetEnumerator();
        enumerator.MoveNext();
        var lockItems = ParseToken(ref enumerator, stationId, isRouteLock, false, false);
        return isRouteLock ? lockItems : [GroupByAndIfMultipleCondition(lockItems)];
    }

    private List<LockItem> ParseToken(ref List<string>.Enumerator enumerator,
        string stationId,
        bool isRouteLock,
        bool isReverse,
        bool isTotalControl)
    {
        List<LockItem> result = [];
        // なぜかCanBeNullなはずなのにそれを無視してしまうので
        // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
        while (enumerator.Current != null)
        {
            var token = enumerator.Current;
            // 括弧とじならbreakし、再起元に判断を委ねる
            if (token is ")" or "]" or "]]" or "}")
            {
                break;
            }

            LockItem item;
            if (token == "{")
            {
                // 意味カッコ
                enumerator.MoveNext();
                var child = ParseToken(ref enumerator, stationId, isRouteLock, isReverse, isTotalControl);
                if (enumerator.Current != "}")
                {
                    throw new InvalidOperationException("}が閉じられていません");
                }

                enumerator.MoveNext();
                result.AddRange(child);
            }
            else if (token == "((")
            {
                // 統括制御(連動図表からではなく、別CSVから取り込みなのでスキップ)
                enumerator.MoveNext();
                ParseToken(ref enumerator, stationId, isRouteLock, isReverse, true);
                if (enumerator.Current != "))")
                {
                    throw new InvalidOperationException("))が閉じられていません");
                }

                enumerator.MoveNext();
            }
            else if (token.StartsWith('['))
            {
                // 別駅所属のObject
                var count = token.Length;
                var targetStationId = StationIdMap[this.stationId][count - 1];
                enumerator.MoveNext();
                var child = ParseToken(ref enumerator, targetStationId, isRouteLock, isReverse, isTotalControl);
                if (enumerator.Current.Length != count || enumerator.Current.Any(c => c != ']'))
                {
                    throw new InvalidOperationException("]が閉じられていません");
                }

                enumerator.MoveNext();
                result.AddRange(child);
            }
            else if (token == "(")
            {
                // 進路鎖錠パース時は進路鎖錠のグループ、それ以外の場合は反位鎖錠
                enumerator.MoveNext();
                var target = ParseToken(
                    ref enumerator,
                    stationId,
                    isRouteLock,
                    !isRouteLock, // 進路鎖状なら定位を渡す、それ以外なら反位を渡す 
                    isTotalControl);
                if (!isRouteLock && target.Count != 1)
                {
                    throw new InvalidOperationException("反位の対象がないか、複数あります");
                }

                if (enumerator.Current is not ")")
                {
                    throw new InvalidOperationException(")が閉じられていません");
                }

                enumerator.MoveNext();
                if (isRouteLock)
                {
                    item = new()
                    {
                        Name = NameAnd,
                        StationId = stationId,
                        IsReverse = isReverse ? NR.Reversed : NR.Normal,
                        Children = target
                    };
                }
                else
                {
                    item = target[0];
                }

                result.Add(item);
            }
            else if (token.StartsWith('但') && token.EndsWith('秒'))
            {
                // 時素条件
                result[^1].TimerSeconds = int.Parse(RegexIntParse()
                    .Match(token)
                    .Value);
                enumerator.MoveNext();
            }
            else if (token == "但")
            {
                // 但条件(左辺 or not右辺)
                var left = result;
                enumerator.MoveNext();
                var right = ParseToken(ref enumerator, stationId, isRouteLock, isReverse, isTotalControl);
                List<LockItem> child =
                [
                    // 左辺
                    GroupByAndIfMultipleCondition(left),
                    // 右辺
                    new()
                    {
                        Name = NameNot,
                        StationId = stationId,
                        IsReverse = isReverse ? NR.Reversed : NR.Normal,
                        Children = [GroupByAndIfMultipleCondition(right)]
                    }
                ];
                result =
                [
                    new()
                    {
                        Name = NameOr,
                        StationId = stationId,
                        IsReverse = isReverse ? NR.Reversed : NR.Normal,
                        Children = child
                    }
                ];
            }
            else if (token == "又は")
            {
                // Todo: or条件 
                var left = result;
                enumerator.MoveNext();
                var right = ParseToken(ref enumerator, stationId, isRouteLock, isReverse, isTotalControl);
                List<LockItem> child =
                [
                    GroupByAndIfMultipleCondition(left),
                ];
                // 右辺がorならそのまま子供を追加
                if (right is [{ Name: NameOr }])
                {
                    child.AddRange(right[0].Children);
                }
                // それ以外ならグルーピングして追加
                else
                {
                    child.Add(GroupByAndIfMultipleCondition(right));
                }

                result =
                [
                    new()
                    {
                        Name = NameOr,
                        StationId = stationId,
                        IsReverse = isReverse ? NR.Reversed : NR.Normal,
                        Children = child
                    }
                ];
            }
            else
            {
                item = new()
                {
                    Name = token,
                    StationId = stationId,
                    isTotalControl = isTotalControl,
                    IsReverse = isReverse ? NR.Reversed : NR.Normal
                };
                result.Add(item);
                enumerator.MoveNext();
            }
        }

        return result;
    }

    private LockItem GroupByAndIfMultipleCondition(List<LockItem> lockItems)
    {
        if (lockItems.Count == 1)
        {
            return lockItems[0];
        }

        return new()
        {
            Children = lockItems,
            Name = NameAnd
        };
    }

    public class LockItem
    {
        public string Name { get; set; }
        public string StationId { get; set; }
        public bool isTotalControl { get; set; }
        public int? RouteLockGroup { get; set; }
        public int? TimerSeconds { get; set; }
        public NR IsReverse { get; set; }
        public List<LockItem> Children { get; set; } = [];
    }

    private string CalcSwitchingMachineName(string start, string stationId)
    {
        return $"{stationId}_W{start}";
    }

    private string CalcLeverName(string start, string stationId)
    {
        if (stationId == "")
        {
            stationId = this.stationId;
        }

        return $"{stationId}_{start.Replace("R", "").Replace("L", "")}";
    }

    private string CalcButtonName(string end, string stationId)
    {
        if (stationId == "")
        {
            stationId = this.stationId;
        }

        return $"{stationId}_{end.Replace("(", "").Replace(")", "")}P";
    }

    private string ConvertHalfWidthToFullWidth(string halfWidth)
    {
        return halfWidth.Replace('ｲ', 'イ').Replace('ﾛ', 'ロ');
    }

    private string CalcRouteName(string start, string end, string stationId)
    {
        if (stationId == "")
        {
            stationId = this.stationId;
        }

        return $"{stationId}_{start}{(end.StartsWith('(') ? "" : end)}";
    }
}