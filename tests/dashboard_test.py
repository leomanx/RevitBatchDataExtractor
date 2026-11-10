"""End-to-end local HTTP/SQLite checks; creates an explicitly synthetic demo."""
import json, pathlib, sqlite3, subprocess, urllib.request, urllib.parse
root = pathlib.Path(__file__).resolve().parents[1]
folder = root / 'test-output' / 'DEMO-synthetic-35-models'
folder.mkdir(parents=True, exist_ok=True)
database = folder / 'extract.sqlite'
with sqlite3.connect(database) as db:
    db.executescript('''
    DROP TABLE IF EXISTS parameters; DROP TABLE IF EXISTS elements; DROP TABLE IF EXISTS errors; DROP TABLE IF EXISTS models;
    CREATE TABLE models(model_id INTEGER PRIMARY KEY,path TEXT,status TEXT,started_utc TEXT,finished_utc TEXT,element_count INTEGER);
    CREATE TABLE elements(model_id INTEGER,element_id INTEGER,unique_id TEXT,kind TEXT,category_id TEXT,category TEXT,family TEXT,type_name TEXT,type_id INTEGER,name TEXT,PRIMARY KEY(model_id,element_id));
    CREATE TABLE parameters(model_id INTEGER,element_id INTEGER,scope TEXT,parameter_key TEXT,name TEXT,storage_type TEXT,has_value INTEGER,raw_value TEXT,display_value TEXT,unit_type_id TEXT);
    CREATE INDEX ix_parameter_element ON parameters(model_id,element_id);
    CREATE TABLE errors(utc TEXT,model_id INTEGER,element_id INTEGER,stage TEXT,exception_type TEXT,message TEXT);
    ''')
    categories = [('Doors','Single-Flush','900 x 2100'),('Walls','Basic Wall','200 mm'),('Floors','Floor','Concrete 150'),('Windows','Fixed','1200 x 1500'),('Rooms','Room','Office'),('Furniture','Desk','1400 x 700')]
    for m in range(1,36):
        status = 'failed' if m in (9,17,31) else 'partial' if m in (4,22) else 'succeeded'
        count = 0 if status=='failed' else 72
        db.execute('INSERT INTO models VALUES(?,?,?,?,?,?)',(m,f'D:\\Demo\\BLDG-{m:02}-Architecture.rvt',status,'2026-11-10T04:00:00Z','2026-11-10T04:02:10Z',count))
        if status!='succeeded': db.execute('INSERT INTO errors VALUES(?,?,?,?,?,?)',('2026-11-10T04:01:00Z',m,None if status=='failed' else 12,'open' if status=='failed' else 'parameter','SyntheticDemoError','DEMO: Model could not be opened' if status=='failed' else 'DEMO: Parameter unavailable'))
        for e in range(1,count+1):
            category,family,type_name=categories[(e+m)%6]
            db.execute('INSERT INTO elements VALUES(?,?,?,?,?,?,?,?,?,?)',(m,e,f'demo-{m}-{e}','type' if e>68 else 'instance','-1',category,family,type_name,900+e%6,'ทดสอบ '+str(e)))
            for scope,key,name,value in [('instance','builtin:ALL_MODEL_MARK','Mark','' if e%7==0 else f'A-{m:02}-{e:03}'),('type','id:123','Fire Rating','60 min' if e%3==0 else '90 min'),('instance','id:456','Comments','</script><img src=x onerror=alert(1)>' if e==1 else 'ตรวจสอบพื้นที่')]:
                db.execute('INSERT INTO parameters VALUES(?,?,?,?,?,?,?,?,?,?)',(m,e,scope,key,name,'String',int(bool(value)),value,value,''))
exe=root/'src/Dashboard/bin/Release/net8.0/RBDE.Dashboard.exe'
process=subprocess.Popen([str(exe),str(database),'--no-browser'],stdout=subprocess.PIPE,stderr=subprocess.PIPE,text=True)
try:
    url=process.stdout.readline().strip()
    assert url.startswith('http://localhost:'),url
    def get(route,**params):
        with urllib.request.urlopen(url+'api/'+route+'?'+urllib.parse.urlencode(params)) as r: return json.load(r)
    data=get('overview'); assert len(data['models'])==35 and data['errors'][0]['count']==5
    data=get('elements',kind='instance'); assert data['total'][0]['count']==32*68 and len(data['rows'])==50
    second=get('elements',kind='instance',page=1); assert second['rows'][0]['element_id']==51
    data=get('elements',model=1,category='Doors'); assert all(r['category']=='Doors' and r['model_id']==1 for r in data['rows'])
    data=get('elements',model=1,parameter='Mark',missing=1); assert data['total'][0]['count']==10
    data=get('elements',model=1,parameter='Fire Rating',scope='type',value='60'); assert data['total'][0]['count']==24
    data=get('elements',model=1,parameter='Fire Rating',scope='instance',value='60'); assert data['total'][0]['count']==0
    assert get('elements',family="' OR 1=1 --")['total'][0]['count']==0
    assert get('elements',parameter='not-exported',missing=1,model=1)['total'][0]['count']==72
    assert len(get('detail',model=1,element=1))==3
    assert get('errors',model=9)['total'][0]['count']==1
    assert len(get('errors')['rows'])==5
    with urllib.request.urlopen(url) as response:
        html=response.read().decode(); assert 'Element explorer' in html and 'Content-Security-Policy' in response.headers
    try: urllib.request.urlopen(url.split('/'+url.split('/')[-2])[0]+'/api/overview'); raise AssertionError('Token bypass')
    except urllib.error.HTTPError as e: assert e.code==404
    print('PASS: 14 dashboard integration checks (summary, paging, filters, missing values, scope, SQL injection, details, errors, HTML, token).')
    print('Synthetic demo:',folder)
finally:
    process.terminate();process.wait(timeout=10)
