/*
 * To change this license header, choose License Headers in Project Properties.
 * To change this template file, choose Tools | Templates
 * and open the template in the editor.
 */
package org.shareabledata;

import java.io.*;

/**
 *
 * @author Malcolm
 */
public class SDatabase {

    public final String name;
    public final SDict<Long, SDbObject> objects;
    public final SDict<String, SDbObject> names;
    public final long curpos;
    static Object files = new Object(); // a lock (not normally ever used)
    protected static SDict<String, AStream> dbfiles = null;
    protected static SDict<String, SDatabase> databases = null;

    SDatabase getRollback() {
        return this;
    }

    protected boolean getCommitted() {
        return true;
    }

    public static SDatabase Open(String path, String fname) throws Exception {
        if (dbfiles != null && dbfiles.Contains(fname)) {
            var r = databases.Lookup(fname);
            if (r == null) {
                throw new Exception("Database is loading");
            }
            return r;
        }
        var db = new SDatabase(fname);
        var fs = new AStream(path, fname);
        if (dbfiles == null) {
            dbfiles = new SDict<>(fname, fs);
        } else {
            dbfiles = dbfiles.Add(fname, fs);
        }
        db = db.Load();
        Install(db);
        return db;
    }

    public static void Install(SDatabase db) {
        if (databases == null) {
            databases = new SDict<>(db.name, db);
        } else {
            databases = databases.Add(db.name, db);
        }
    }

    public SDbObject Lookup(long pos) {
        return (objects == null) ? null : objects.Lookup(pos);
    }

    public SRecord Get(long pos) throws Exception {
        var s = _Get(pos);
        SRecord rc = null;
        if (s != null && s.type == Types.SRecord || s.type == Types.SUpdate) {
            rc = (SRecord) s;
        }
        if (rc == null) {
            throw new Exception("Record " + pos + " never defined");
        }
        var tb = (STable) Lookup(rc.table);
        if (tb == null) {
            throw new Exception("Table " + rc.table + " has been dropped");
        }
        if (tb.rows == null || !tb.rows.Contains(rc.Defpos())) {
            throw new Exception("Record " + pos + " has been dropped");
        }
        return (SRecord) _Get(tb.rows.Lookup(rc.Defpos()));
    }

    public Serialisable _Get(long pos) throws Exception {
        return new Reader(dbfiles.Lookup(name), pos)._Get(this);
    }

    SDatabase(String fname) {
        name = fname;
        objects = null;
        names = null;
        curpos = 0;
    }

    protected SDatabase(SDatabase db) {
        name = db.name;
        objects = db.objects;
        names = db.names;
        curpos = db.curpos;
    }

    // CRUD on Records changes indexes as well as table, so we need this
    protected SDatabase(SDatabase db, SDict<Long, SDbObject> obs, 
            SDict<String,SDbObject> nms,long c) {
        name = db.name;
        objects = obs;
        names = nms;
        curpos = c;
    }

    protected SDatabase(SDatabase db, STable t, long c) {
        name = db.name;
        if (db.names == null) {
            objects = new SDict<>(t.uid, t);
            names = new SDict<>(t.name, t);
        } else {
            objects = db.objects.Add(t.uid, t);
            names = db.names.Add(t.name, t);
        }
        curpos = c;
    }

    protected SDatabase(SDatabase db, SAlter a, long c) throws Exception {
        name = db.name;
        if (a.parent == 0) {
            var o = db.Lookup(a.defpos);
            if (o == null || o.type != Types.STable) {
                throw new Exception("Not a Table");
            }
            var ot = (STable) o;
            var nt = new STable(ot, a.name);
            objects = db.objects.Add(a.defpos, nt);
            names = db.names.Remove(ot.name).Add(a.name, nt);
        } else {
            var o = db.Lookup(a.parent);
            if (o == null || o.type != Types.STable) {
                throw new Exception("Not a Table");
            }
            var ot = (STable) o;
            var oc = (ot.cols == null) ? null : ot.cols.Lookup(a.defpos);
            var nc = new SColumn((SColumn)oc, a.name, a.dataType);
            var nt = ot.Add(nc);
            objects = db.objects.Add(a.defpos, nt);
            names = db.names.Add(a.name, nt);
        }
        curpos = c;
    }

    protected SDatabase(SDatabase db, SDrop d, long c) {
        name = db.name;
        if (d.parent == 0) {
            var ot = (STable) db.Lookup(d.drpos);
            objects = db.objects.Remove(d.drpos);
            names = db.names.Remove(ot.name);
        } else {
            var ot = (STable) db.Lookup(d.parent);
            STable nt = null;
            try {
                nt = ot.Remove(d.drpos);
            } catch (Exception e) {
                objects = db.objects;
                names = db.names;
                curpos = c;
                return;
            }
            objects = db.objects.Add(d.parent, nt);
            names = db.names;
        }
        curpos = c;
    }

    protected SDatabase(SDatabase db, SView v, long c) {
        name = db.name;
        objects = db.objects.Add(v.uid, v);
        names = db.names.Add(v.name, v);
        curpos = c;
    }

    protected SDatabase(SDatabase db, SIndex x, long c) throws Exception {
        name = db.name;
        var tb = (STable) db.Lookup(x.table);
        if (tb.rows != null) {
            for (var b = tb.rows.First(); b != null; b = b.Next()) {
                x = x.Add(db.Get(b.getValue().val), b.getValue().val);
            }
        }
        objects = db.objects.Add(x.uid, x);
        names = db.names;
        curpos = c;
    }

    public AStream File() {
        return dbfiles.Lookup(name);
    }

    SDatabase Load() throws Exception {
        var rd = new Reader(dbfiles.Lookup(name), 0);
        var db = this;
        for (var s = (SDbObject) rd._Get(this); s != null; s = (SDbObject) rd._Get(db)) {
            db = db._Add(s, s.uid);
        }
        return db;
    }

    public SDatabase _Add(SDbObject s, long p) throws Exception {
        switch (s.type) {
            case Types.STable:
                return Install((STable) s, p);
            case Types.SColumn:
                return Install((SColumn) s, p);
            case Types.SUpdate:
                return Install((SUpdate) s, p);
            case Types.SRecord:
                return Install((SRecord) s, p);
            case Types.SDelete:
                return Install((SDelete) s, p);
            case Types.SAlter:
                return Install((SAlter) s, p);
            case Types.SDrop:
                return Install((SDrop) s, p);
            //        case Types.SView: return Install((SView)s, p);
            case Types.SIndex:
                return Install((SIndex) s, p);
        }
        return this;
    }
    /// <summary>
    /// Only for testing environments!
    /// </summary>

    public void Close() throws IOException {
        synchronized (files) {
            AStream f = dbfiles.Lookup(name);
            databases = databases.Remove(name);
            dbfiles = dbfiles.Remove(name);
            f.Close();
        }
    }

    protected SDatabase Install(STable t, long p) {
        return new SDatabase(this, t, p);
    }

    protected SDatabase Install(SColumn c, long p) throws Exception {
        var tb =(STable) objects.Lookup(c.table);
        tb = tb.Add(c);
        return new SDatabase(this, tb, p);
    }

    protected SDatabase Install(SRecord r, long p) throws Exception {
        var obs = objects;
        var st = ((STable) Lookup(r.table)).Add(r);
        obs = obs.Add(r.table, st);
        var nms = names.Add(st.name,st);
        for (var b = obs.First(); b != null; b = b.Next()) {
            if (b.getValue().val.type == Types.SIndex) {
                var x = (SIndex) b.getValue().val;
                if (x.table == r.table) {
                    var k = x.uid;
                    var v = x.Add(r, r.uid);
                    obs = obs.Add(k, v);
                }
                if (x.references == r.table && !x.Contains(r)) {
                    throw new Exception("Referential constraint");
                }
            }
        }
        return new SDatabase(this, obs, nms, p);
    }

    protected SDatabase Install(SUpdate u, long c) throws Exception {
        var obs = objects;
        var st = ((STable) Lookup(u.table)).Add(u);
        SRecord sr = null;
        obs = obs.Add(u.table, st);
        var nms = names.Add(st.name, st);
        for (var b = obs.First(); b != null; b = b.Next()) {
            if (b.getValue().val.type == Types.SIndex) {
                var x = (SIndex) b.getValue().val;
                if (x.table == u.table) {
                    if (sr == null) {
                        sr = Get(u.defpos);
                    }
                    obs = obs.Add(x.uid, x.Update(sr, u, c));
                }
                if (x.references == u.table && !x.Contains(u)) {
                    throw new Exception("Referential constraint");
                }
            }
        }
        return new SDatabase(this, obs, nms, c);
    }

    protected SDatabase Install(SDelete d, long p) throws Exception {
        var obs = objects;
        var st = ((STable) Lookup(d.table)).Remove(d.delpos);
        SRecord sr = null;
        obs = obs.Add(d.table, st);
        var nms = names.Add(st.name,st);
        for (var b = obs.First(); b != null; b = b.Next()) {
            if (b.getValue().val.type == Types.SIndex) {
                var x = (SIndex) b.getValue().val;
                if (x.table == d.table) {
                    if (sr == null) {
                        sr = Get(d.delpos);
                    }
                    obs = obs.Add(x.uid, x.Remove(sr, p));
                }
                if (x.references == d.table && x.Contains(sr)) {
                    throw new Exception("Referential constraint");
                }
            }
        }
        return new SDatabase(this, obs, nms, p);
    }

    protected SDatabase Install(SAlter a, long c) throws Exception {
        return new SDatabase(this, a, c);
    }

    protected SDatabase Install(SDrop d, long c) {
        return new SDatabase(this, d, c);
    }

    protected SDatabase Install(SView v, long c) {
        return new SDatabase(this, v, c);
    }

    protected SDatabase Install(SIndex x, long c) throws Exception {
        return new SDatabase(this, x, c);
    }

    public STransaction Transact(boolean auto) {
        return new STransaction(this, auto);
    }

    public SDatabase MaybeAutoCommit(STransaction tr) throws Exception {
        return tr.autoCommit ? tr.Commit() : tr;
    }

    public SDatabase Rollback() {
        return this;
    }

    STable GetTable(String tn) {
        var ob = names.Lookup(tn);
        return (ob != null && ob.type == Types.STable)
                ? (STable) ob : null;
    }

    SIndex GetPrimaryIndex(long t) {
        for (var b = objects.First(); b != null; b = b.Next()) {
            if (b.getValue().val.type == Types.SIndex) {
                var x = (SIndex) b.getValue().val;
                if (x.table == t) {
                    return x;
                }
            }
        }
        return null;
    }
}
