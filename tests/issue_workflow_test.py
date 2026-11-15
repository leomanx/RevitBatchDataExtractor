"""Exercise the real Dashboard HTTP workflow against an isolated synthetic warehouse."""
import json, pathlib, sqlite3, subprocess, tempfile, urllib.request, urllib.error, urllib.parse, zipfile, io, xml.etree.ElementTree as ET
from contextlib import closing
root=pathlib.Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix='rbde-workflow-') as temp:
    base=pathlib.Path(temp); run=base/'batch-20261113-000000';run.mkdir()
    with closing(sqlite3.connect(run/'extract.sqlite')) as db:
        db.execute('CREATE TABLE models(model_id INTEGER PRIMARY KEY,path TEXT,project_id TEXT,lineage_id TEXT)')
        db.execute("INSERT INTO models VALUES(1,'test.rvt','project-a','lineage-a')")
        db.execute('CREATE TABLE issues(issue_id INTEGER PRIMARY KEY,model_id INTEGER,rule_id TEXT,requirement_id TEXT,parameter_name TEXT,message TEXT,actual_value TEXT,expected_rule TEXT,severity TEXT,lifecycle_state TEXT,unique_id TEXT,evaluation_status TEXT)')
        db.execute("INSERT INTO issues VALUES(1,1,'R1','EIR-1','Mark','Missing mark','','required','High','New','uid-1','Failed')")
        db.execute("INSERT INTO issues VALUES(2,1,'R2','EIR-2','Area','Excluded','','required','Low','New','uid-2','NotEvaluated')")
        db.commit()
    with closing(sqlite3.connect(base/'warehouse.sqlite')) as db:
        db.execute('CREATE TABLE issue_registry(project_id TEXT,lineage_id TEXT,unique_id TEXT,rule_id TEXT,parameter_name TEXT,discipline TEXT,severity TEXT,status TEXT,first_seen_run TEXT,last_seen_run TEXT,closed_run TEXT,reopened_count INTEGER, PRIMARY KEY(project_id,lineage_id,unique_id,rule_id,parameter_name))')
        db.execute("INSERT INTO issue_registry VALUES('project-a','lineage-a','uid-1','rule-1','Mark','AR','High','Open','r1','r1','',0)")
        db.commit()
    dll=root/'test-output/workflow-build/RBDE.Dashboard.dll'
    p=subprocess.Popen(['dotnet',str(dll),str(run/'extract.sqlite'),'--no-browser'],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
    try:
        url=p.stdout.readline().strip()+'api/issue-workflow?'+urllib.parse.urlencode({'model':1,'uid':'uid-1','rule':'rule-1','parameter':'Mark'})
        def call(method='GET',payload=None):
            req=urllib.request.Request(url,data=None if payload is None else json.dumps(payload).encode(),headers={'Content-Type':'application/json'},method=method)
            with urllib.request.urlopen(req) as response:return json.load(response)
        assert call()['status']=='Open'
        data={'actor':'Coordinator','assignee':'AR Team','dueDate':'2026-11-20','comment':'Check door schedule','signoff':''}
        assert call('POST',data)['assignee']=='AR Team'
        with closing(sqlite3.connect(base/'warehouse.sqlite')) as db:
            assert db.execute('SELECT count(*) FROM issue_workflow_events').fetchone()[0]==3
            db.execute("UPDATE issue_registry SET status='Closed' WHERE unique_id='uid-1'")
            db.commit()
        with urllib.request.urlopen(url.split('api/issue-workflow')[0]+'api/fixed-issues') as response:
            assert len(json.load(response))==1
        data['signoff']='BIM Lead'
        assert call('POST',data)['reviewer_signoff']=='BIM Lead'
        with urllib.request.urlopen(url.split('api/issue-workflow')[0]+'api/fixed-issues') as response:
            assert json.load(response)==[]
        data['assignee']='';data['signoff']=''
        assert call('POST',data)['assignee']==''
        with urllib.request.urlopen(url.split('api/issue-workflow')[0]+'api/export-bcf?model=1') as response:
            archive=zipfile.ZipFile(io.BytesIO(response.read()));names=archive.namelist()
            assert 'bcf.version' in names and len([x for x in names if x.endswith('markup.bcf')])==1
            assert ET.fromstring(archive.read('bcf.version')).attrib['VersionId']=='2.1'
            assert 'Missing mark' in archive.read(next(x for x in names if x.endswith('markup.bcf'))).decode()
        with closing(sqlite3.connect(base/'warehouse.sqlite')) as db:
            assert db.execute('SELECT count(*) FROM issue_workflow_events').fetchone()[0]>=6
        print('PASS: issue assignment, audit trail, fixed-issue sign-off, BCF issue archive, and exclusion of NotEvaluated.')
    finally:p.terminate();p.wait(timeout=10)
