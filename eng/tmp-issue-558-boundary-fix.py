#!/usr/bin/env python3
from pathlib import Path


def replace_once(path: str, old: str, new: str) -> None:
    file = Path(path)
    text = file.read_text(encoding="utf-8")
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{path}: expected one replacement, found {count}")
    file.write_text(text.replace(old, new), encoding="utf-8")


replace_once(
    "src/SharpLink.Runtime/Codec/CodecHelpers.cs",
    '''    public static DateTime CreateDateTime(long binaryData)\n    {\n        try\n        {\n            return DateTime.FromBinary(binaryData);\n        }\n        catch (ArgumentException ex)\n        {\n            throw new SharpLinkException(SharpLinkErrorCode.DataLoss, "Invalid DateTime payload.", ex);\n        }\n    }\n\n''',
    "")

replace_once(
    "src/SharpLink.Runtime/Codec/CodecHelpers.cs",
    '''        if (typeof(T) == typeof(DateTime))\n        {\n            var typed = MemoryMarshal.Cast<T, DateTime>(values);\n            for (var index = 0; index < typed.Length; index++)\n            {\n                var value = typed[index];\n                _ = CreateDateTime(Unsafe.As<DateTime, long>(ref value));\n            }\n            return;\n        }''',
    '''        if (typeof(T) == typeof(DateTime))\n        {\n            var typed = MemoryMarshal.Cast<T, DateTime>(values);\n            for (var index = 0; index < typed.Length; index++)\n                _ = DateTimeCodec.ValidateRaw(typed[index]);\n            return;\n        }''')

replace_once(
    "test/SharpLink.UnitTests/Validation/CodecValidationProbe.cs",
    '''        var input = Environment.GetEnvironmentVariable("SHARPLINK_CODEC_INPUT");\n        if (string.IsNullOrEmpty(input))\n        {\n            var kind = Enum.Parse<DateTimeKind>(Environment.GetEnvironmentVariable("SHARPLINK_DATE_KIND")!);\n            var value = new DateTime(2026, 1, 15, 12, 34, 56, kind);''',
    '''        var input = Environment.GetEnvironmentVariable("SHARPLINK_CODEC_INPUT");\n        var dateCase = Environment.GetEnvironmentVariable("SHARPLINK_DATE_CASE") ?? "normal";\n        if (string.IsNullOrEmpty(input))\n        {\n            var kind = Enum.Parse<DateTimeKind>(Environment.GetEnvironmentVariable("SHARPLINK_DATE_KIND")!);\n            var value = dateCase switch\n            {\n                "normal" => new DateTime(2026, 1, 15, 12, 34, 56, kind),\n                "max-local" when kind == DateTimeKind.Local =>\n                    new DateTime(DateTime.MaxValue.Ticks - TimeSpan.TicksPerHour, DateTimeKind.Local),\n                _ => throw new InvalidOperationException($"Unsupported DateTime validation case '{dateCase}' for {kind}.")\n            };''')

replace_once(
    "test/SharpLink.UnitTests/Validation/CodecValidationProbe.cs",
    '''                operation = "write",\n                zone = TimeZoneInfo.Local.Id,''',
    '''                operation = "write",\n                dateCase,\n                zone = TimeZoneInfo.Local.Id,''')

replace_once(
    "test/SharpLink.UnitTests/Validation/CodecValidationProbe.cs",
    '''            operation = "read",\n            sourceZone = root.GetProperty("zone").GetString(),\n            zone = TimeZoneInfo.Local.Id,\n            offsetTicks = TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 1, 15)).Ticks,''',
    '''            operation = "read",\n            dateCase,\n            sourceZone = root.GetProperty("zone").GetString(),\n            zone = TimeZoneInfo.Local.Id,\n            offsetTicks = TimeZoneInfo.Local.GetUtcOffset(decodedScalar).Ticks,''')

replace_once(
    "test/SharpLink.UnitTests/Validation/CodecValidationProbe.cs",
    '''    private static bool Same(DateTime left, DateTime right)\n        => left.Ticks == right.Ticks && left.Kind == right.Kind &&\n           left.ToUniversalTime().Ticks == right.ToUniversalTime().Ticks;''',
    '''    private static bool Same(DateTime left, DateTime right)\n        => left.Ticks == right.Ticks && left.Kind == right.Kind;''')

replace_once(
    "eng/validate-codec-semantics.py",
    '''def worker(name, method, directory, zone="Etc/UTC", kind="Local", source=None):\n    result = directory / (name + ".json")\n    result.unlink(missing_ok=True)\n    environment = dict(os.environ, TZ=zone, SHARPLINK_DATE_KIND=kind,\n                       SHARPLINK_VALIDATION_OUTPUT=str(result))''',
    '''def worker(name, method, directory, zone="Etc/UTC", kind="Local", source=None, date_case="normal"):\n    result = directory / (name + ".json")\n    result.unlink(missing_ok=True)\n    environment = dict(os.environ, TZ=zone, SHARPLINK_DATE_KIND=kind,\n                       SHARPLINK_DATE_CASE=date_case, SHARPLINK_VALIDATION_OUTPUT=str(result))''')

replace_once(
    "eng/validate-codec-semantics.py",
    '''    performance = None\n    try:\n        performance, _ = worker("datetimeoffset-fragmentation", "DateTimeOffsetFragmentation", directory)''',
    '''    boundary_rows = []\n    if args.mode == "regression":\n        try:\n            produced, source = worker(\n                "boundary-max-local-write", "DateTimeCrossZone", directory,\n                "Etc/UTC", "Local", date_case="max-local")\n            if not produced["invariant"]:\n                raise RuntimeError("max-local: same-process roundtrip control failed")\n            report, _ = worker(\n                "boundary-max-local-to-Asia-Tokyo", "DateTimeCrossZone", directory,\n                "Asia/Tokyo", "Local", source, date_case="max-local")\n            raw_matched = matches_raw_contract(report)\n            report.update(boundaryCase="max-local", rawContractMatched=raw_matched,\n                          selectedModePassed=raw_matched)\n            failed |= not raw_matched\n            boundary_rows.append(report)\n            print(json.dumps(report), flush=True)\n        except Exception as error:\n            failed = True\n            errors.append(str(error))\n            print(f"INFRASTRUCTURE FAILURE: {error}", file=sys.stderr, flush=True)\n\n    performance = None\n    try:\n        performance, _ = worker("datetimeoffset-fragmentation", "DateTimeOffsetFragmentation", directory)''')

replace_once(
    "eng/validate-codec-semantics.py",
    '''                   dateTime=rows, performance=performance, infrastructureErrors=errors,\n                   note=("Green regression means DateTime scalar, nullable and built-in collection paths preserve "\n                         "raw ticks + Kind across zones; DateTimeOffset timings remain measurement evidence only."))''',
    '''                   dateTime=rows, dateTimeBoundary=boundary_rows, performance=performance,\n                   infrastructureErrors=errors,\n                   note=("Green regression means DateTime scalar, nullable and built-in collection paths preserve "\n                         "raw ticks + Kind across zones, including a Local value one hour below DateTime.MaxValue "\n                         "decoded in UTC+9; DateTimeOffset timings remain measurement evidence only."))''')

replace_once(
    "docs/validation/codec-semantics.md",
    '''Regression mode requires every DateTime route to preserve the producer's `ticks + Kind`. It records UTC ticks as evidence but deliberately does not require Local/Unspecified UTC ticks to remain equal across zones. This catches any future reintroduction of instant-preserving scalar semantics while collections remain raw.\n\nThe test input is January 15, 2026, away from DST transitions. DST ambiguity/invalid local times, cross-runtime layout compatibility and big-endian compatibility are outside this #558 regression.''',
    '''Regression mode requires every DateTime route to preserve the producer's `ticks + Kind`. It records UTC ticks as evidence but deliberately does not require Local/Unspecified UTC ticks to remain equal across zones. This catches any future reintroduction of instant-preserving scalar semantics while collections remain raw.\n\nA dedicated boundary regression also produces a valid `Local` value one hour below `DateTime.MaxValue` in UTC and decodes the exact scalar/nullable/collection payloads in Tokyo (UTC+9). Every route must accept the value and preserve its raw ticks + Kind. This specifically prevents collection validation from reusing `DateTime.FromBinary`, whose local-time adjustment can overflow near `DateTime.MaxValue` even though the raw `DateTime` itself is valid.\n\nThe ordinary matrix input is January 15, 2026, away from DST transitions. DST ambiguity/invalid local times, cross-runtime layout compatibility and big-endian compatibility are outside this #558 regression.''')

print("issue 558 boundary patch applied")
