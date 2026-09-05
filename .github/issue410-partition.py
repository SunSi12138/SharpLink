from pathlib import Path

path = Path('src/SharpLink.Server/Admission/SharpLinkAdmissionController.cs')
text = path.read_text(encoding='utf-8')

old_create = '                    createdRate = AdmissionRateState.Create(target, _timeProvider, source.Rate);\n'
new_create = '                    createdRate = AdmissionRateState.Create(target, _timeProvider);\n'
if text.count(old_create) != 1:
    raise RuntimeError(f'partition generation create: expected one match, found {text.count(old_create)}')
text = text.replace(old_create, new_create, 1)

start_marker = '        if (transition.Source.Rate is { } sourceRate &&\n'
end_marker = '        if (transition.ResizePermitLimit is { } permitLimit)\n'
start = text.index(start_marker)
end = text.index(end_marker, start)
text = text[:start] + text[end:]

path.write_text(text, encoding='utf-8')
