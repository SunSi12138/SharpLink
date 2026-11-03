"""CI-only finite harmless process probes. Never invoked by Python unit tests."""
import os
from pathlib import Path
import sys
import tempfile
import process_owner as p


def main():
    p.enable_subreaper();os.umask(0o077)
    work=Path(tempfile.mkdtemp(prefix='sl-owner-'))
    probes=[('success','pass',False),
            ('timeout','import time;time.sleep(60)',True),
            ('escaped-child',"import os,time,signal;r,w=os.pipe();pid=os.fork();\nif pid==0:\n os.close(r);os.setsid();signal.signal(signal.SIGTERM,signal.SIG_IGN);os.write(w,b'R');os.close(w);time.sleep(60)\nelse:\n os.close(w);assert os.read(r,1)==b'R';os.close(r);os._exit(0)",True)]
    for label,source,negative in probes:
        logs=work/label;logs.mkdir()
        try:
            p.run_owned(label,[sys.executable,'-B','-c',source],work,{k:os.environ[k] for k in ('PATH','LANG','LC_ALL','LD_LIBRARY_PATH') if k in os.environ},logs,2)
        except p.CommandFailure as failure:
            p.require(negative and failure.outcome['remainingOwned']=={} and failure.outcome['exitCode'] is not None,
                      'Owner probe did not join')
            row=failure.outcome
            p.require(row['cancelled'] is False and row['reportingErrors']==[] and
                row['cleanupErrors']==['OwnedProcessesAfterCommand'] and
                row['errors']==(['TimeoutError'] if label=='timeout' else []) and
                row['timedOut'] is (label=='timeout') and
                (label=='timeout' or row['exitCode']==0),'Wrong probe failure')
        else:p.require(not negative,'Negative owner probe incorrectly passed')
    print('FINITE_HARMLESS_OWNER_PROBES_PASS: 3; not product qualification')


if __name__=='__main__':
    try:main()
    except BaseException:
        print('FINITE_HARMLESS_OWNER_PROBES_FAILED')
        raise SystemExit(1)
