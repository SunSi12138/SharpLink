def exception_graph(errors):
    """Private graph; duplicate root/group occurrences remain distinct ordered edges.

    Node IDs preserve shared identity/cycles, NOT occurrence deduplication. A failed
    message formatter becomes an independent root and is not recursively formatted.
    Tracebacks preserve every frame/file/function/line/instruction offset without
    invoking repr on locals, arbitrary object state or traceback formatter code.
    """
    nodes = []; refs = {}; queue = []; diagnostics = set()
    def ref(error):
        key = id(error)
        if key not in refs:
            refs[key] = len(nodes); nodes.append(None); queue.append(error)
        return refs[key]
    def diagnostic(error):
        diagnostics.add(id(error)); errors.append(error); return ref(error)
    roots = [ref(error) for error in errors]
    index = 0
    while index < len(queue):
        error = queue[index]; index += 1
        node = {'type': type(error).__module__ + '.' + type(error).__qualname__}
        if id(error) in diagnostics:
            node['messageUnavailable'] = 'independent formatter failure; not formatted recursively'
        else:
            try:
                node['message'] = str(error)
            except BaseException as failure:
                node['messageFailure'] = diagnostic(failure)
        tb = error.__traceback__; frames = []
        while tb is not None:
            frames.append({'file': tb.tb_frame.f_code.co_filename, 'function': tb.tb_frame.f_code.co_name,
                           'line': tb.tb_lineno, 'instruction': tb.tb_lasti})
            tb = tb.tb_next
        node['traceback'] = frames
        node['cause'] = ref(error.__cause__) if error.__cause__ is not None else None
        node['context'] = ref(error.__context__) if error.__context__ is not None else None
        node['suppressContext'] = error.__suppress_context__
        if isinstance(error, BaseExceptionGroup):
            node['members'] = [ref(member) for member in error.exceptions]
        # Public owner contract: preserve its terminal outcome plus every original
        # independent cause, including those not installed as Python __cause__.
        if hasattr(error, 'outcome') and hasattr(error, 'causes'):
            node['ownerOutcome'] = error.outcome
            node['ownerCauses'] = [ref(member) for member in error.causes]
        nodes[refs[id(error)]] = node
    roots = [ref(error) for error in errors]
    return {'schemaVersion': 1, 'orderedRootOccurrences': roots, 'nodes': nodes,
            'qualification': 'Private original exception graph; repeated edges are independent occurrences. No PASS implied.'}

