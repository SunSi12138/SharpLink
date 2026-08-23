from pathlib import Path

path = Path('.github/issue-287-review5-p2a.py')
text = path.read_text()
text = text.replace('=> new(true, timestamp);', '=> new(timestamp);')
text = text.replace(
    '''        return new(\n            true,\n            SharpLinkTime.AddDuration(timestampNow, timeBudget, timestampFrequency));''',
    '''        return new RpcDeadline(\n            timeBudget == TimeSpan.Zero\n                ? timestampNow\n                : SharpLinkTime.AddDuration(timestampNow, timeBudget, timestampFrequency));''')
text = text.replace(
    '''        TimeSpan? selectedTimeout = methodHasTimeout\n            ? methodTimeout\n            : allowDefaultTimeout\n                ? _requestTimeout\n                : null;\n\n        var ambientCall = SharpLinkCallContext.Current;''',
    '''        var selectedTimeout = methodTimeout;\n        if (selectedTimeout is null && (includeClientDefault || hasMethodTimeout) && _hasRequestTimeout)\n            selectedTimeout = _requestTimeoutValue;\n\n        var ambientCall = SharpLinkCallContext.Current;''')
path.write_text(text)
