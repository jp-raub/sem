"""Coverage contract: added UI checks are mandatory without replacing full acceptance."""
from pathlib import Path
import re
root=Path(__file__).resolve().parent.parent
workflow=(root/'.github/workflows/main-integration.yml').read_text()
fixture=(root/'scripts/acceptance-browser-fixture.sh').read_text()
driver=(root/'scripts/e2e.cjs').read_text()
def verify(w,f,d):
 integration=w.split('  integration-checks:',1)[1].split('\n  acceptance-browser:',1)[0]
 browser=w.split('\n  acceptance-browser:',1)[1]
 assert 'timeout-minutes: 40' in integration and 'timeout-minutes: 30' in browser
 steps=re.split(r'(?m)^      - ',integration)
 target=[s for s in steps if 'name: Verify unified timetable UI in an independent fresh fixture' in s]
 assert len(target)==1
 assert 'SEM_ACCEPTANCE_FOCUS: timetable-editor-ui' in target[0]
 assert 'run: bash scripts/acceptance-browser-fixture.sh' in target[0]
 assert not re.search(r'(?m)^        (?:if:|continue-on-error:)',target[0])
 assert 'run: bash scripts/integration-checks.sh' in integration
 assert 'run: bash scripts/acceptance-browser-fixture.sh' in browser
 assert 'SEM_ACCEPTANCE_FOCUS:' not in browser
 assert 'timetable-editor-ui) export SEM_TIMETABLE_EDITOR_UI_CHECKS=1 ;;' in f
 full=re.search(r'(?m)^ full\)(.*);;',f).group(1)
 for flag in ['NOTIFICATION_UI','THEME_NAVIGATION','EDIT_GUARD','PUBLIC_ANSWER','EVENT_HEADER','EVENT_DESCRIPTION','EVENT_LOCATION','EVENT_MATRIX','COMMENT','ACCEPTANCE_WORKFLOW']:
  assert 'SEM_'+flag+'_CHECKS=1' in full
 assert 'SEM_ACCEPTANCE_RETENTION_COMMAND="$run/restore.sh"' in full
 assert 'export SEM_STORAGE_UPGRADE_COMMAND="$run/restore.sh"' in f
 assert "if(process.env.SEM_TIMETABLE_EDITOR_UI_CHECKS)await phase('timetable-editor-ui-checks',()=>require('./timetable-editor-ui-checks.cjs')({p,g,ok}));" in d
 assert 'exit "$status"' in f
 assert '!verification/acceptance-native/**' in integration
 for private in ['e2e-error.txt','host.log','data.db','key.pem']:
  assert private not in integration
verify(workflow,fixture,driver)
mutations=[workflow.replace('SEM_ACCEPTANCE_FOCUS: timetable-editor-ui','SEM_ACCEPTANCE_FOCUS: notification-ui'),workflow.replace('timeout-minutes: 30','timeout-minutes: 45'),workflow.replace('        run: bash scripts/acceptance-browser-fixture.sh','        continue-on-error: true\n        run: bash scripts/acceptance-browser-fixture.sh',1),workflow.replace('        run: bash scripts/acceptance-browser-fixture.sh','        if: false\n        run: bash scripts/acceptance-browser-fixture.sh',1)]
for w in mutations:
 try:verify(w,fixture,driver)
 except AssertionError:continue
 raise AssertionError('Unsafe workflow mutation accepted')
print('Unified timetable coverage contract passed: actual workflow + 4 unsafe mutations rejected')
