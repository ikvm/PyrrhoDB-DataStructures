/*
 * To change this license header, choose License Headers in Project Properties.
 * To change this template file, choose Tools | Templates
 * and open the template in the editor.
 */
package org.shareabledata;

import java.io.*;
import java.nio.charset.Charset;
import java.util.*;

/**
 * This class is not shareable
 *
 * @author Malcolm
 */
public class AStream extends StreamBase {

    public RandomAccessFile file;
    public String filename;
    long wposition = 0;
    long length = 0;
    SDict<Long, Long> uids = null; // used for movement of SDbObjects
    SDict<Long,Serialisable> commits = null;
    @Override
    protected long getLength() {
        return length;
    }

    public AStream(String path, String fn) throws IOException {
        file = new RandomAccessFile(new File(path, fn), "rws");
        filename = fn;
        wbuf = null;
        length = file.length();
        wposition = length;
        file.seek(0);
    }

    Serialisable Lookup(SDatabase db, long pos)
    {
        pos = Fix(pos);
        if (pos>=STransaction._uid)
            return db.objects.Lookup(pos);
        if (commits!=null && commits.Contains(pos))
            return commits.Lookup(pos);
        try {
            return new Reader(this,pos)._Get(db);
        } catch(Exception e)
        {
            throw new Error("invalid log at "+pos);
        }
    }

    long Fix(long pos) 
    {
        if (uids != null && uids.Contains(pos)) {
            pos = uids.Lookup(pos);
        }
        return pos;
    }

    long pos() {
        return length + wbuf.wpos;
    }

    public SDatabase Commit(SDatabase db, STransaction tr) throws Exception {
        commits = null;
        wbuf = new Buffer(this);
        uids = new SDict<Long, Long>(-1L, -1L);
        for (var b = tr.objects.PositionAt(STransaction._uid); b != null; b = b.Next()) {
            var bs = b.getValue();
            switch (bs.val.type) {
                case Types.STable: {
                    var st = (STable) b.getValue().val;
                    var nt = new STable(st, this);
                    db = db._Add(nt, pos());
                    if (commits==null)
                        commits = new SDict<Long,Serialisable>(nt.uid,nt);
                    else
                        commits = commits.Add(nt.uid, nt);
                    break;
                }
                case Types.SColumn: {
                    var sc = (SColumn) bs.val;
                    var st = (STable) Lookup(db, Fix(sc.table));
                    var nc = new SColumn(sc, this);
                    db = db._Add(nc, pos());
                    if (commits==null)
                        commits = new SDict<Long,Serialisable>(nc.uid,nc);
                    else
                        commits = commits.Add(nc.uid, nc);
                    break;
                }
                case Types.SRecord: {
                    var sr = (SRecord) bs.val;
                    var st = (STable) Lookup(db, Fix(sr.table));
                    var nr = new SRecord(db, sr, this);
                    db = db._Add(nr, pos());
                    if (commits==null)
                        commits = new SDict<Long,Serialisable>(nr.uid,nr);
                    else
                        commits = commits.Add(nr.uid, nr);
                    break;
                }
                case Types.SDelete: {
                    var sd = (SDelete) bs.val;
                    var st = (STable) Lookup(db, Fix(sd.table));
                    var nd = new SDelete(sd, this);
                    db = db._Add(nd, pos());
                    if (commits==null)
                        commits = new SDict<Long,Serialisable>(nd.uid,nd);
                    else
                        commits = commits.Add(nd.uid, nd);
                    break;
                }
                case Types.SUpdate: {
                    var sr = (SUpdate) b.getValue().val;
                    var st = (STable) Lookup(db, Fix(sr.table));
                    var nr = new SUpdate(db, sr, this);
                    db = db._Add(nr, pos());
                    if (commits==null)
                        commits = new SDict<Long,Serialisable>(nr.uid,nr);
                    else
                        commits = commits.Add(nr.uid, nr);
                    break;
                }
                case Types.SAlter: {
                    var sa = new SAlter((SAlter) b.getValue().val, this);
                    db = db._Add(sa, pos());
                    if (commits==null)
                        commits = new SDict<Long,Serialisable>(sa.uid,sa);
                    else
                        commits = commits.Add(sa.uid, sa);
                    break;
                }
                case Types.SDrop: {
                    var sd = new SDrop((SDrop) b.getValue().val, this);
                    db = db._Add(sd, pos());
                    if (commits==null)
                        commits = new SDict<Long,Serialisable>(sd.uid,sd);
                    else
                        commits = commits.Add(sd.uid, sd);
                    break;
                }
                case Types.SIndex: {
                    var si = new SIndex((SIndex) b.getValue().val, this);
                    db = db._Add(si, pos());
                    if (commits==null)
                        commits = new SDict<Long,Serialisable>(si.uid,si);
                    else
                        commits = commits.Add(si.uid, si);
                    break;
                }
            }
        }
        Flush();
        SDatabase.Install(db);
        return db;
    }
    void CommitDone()
    {
        uids = null;
        commits = null;
    }
    public void Close() throws IOException {
        file.close();
    }

    protected boolean GetBuf(Buffer b) {
        synchronized(file)
        {
            if (b.start > wposition) {
                throw new Error("File overrun");
     //           return false;
            }
            try
            {
                file.seek(b.start);
                var n = length - b.start;
                if (n > Buffer.Size) {
                    n = Buffer.Size;
                }
                b.len = file.read(b.buf, 0, (int) n);
                    return b.len > 0;
            } catch(Exception e)
            {
                throw new Error("In Get");
            }
        }
    }

    protected void PutBuf(Buffer b){
        try {
            var p = file.length();
            file.seek(p);
            file.write(b.buf, 0, b.wpos);
            length = p + b.wpos;
            wposition = length;
            b.wpos = 0;
        } catch(Exception e)
        {
            throw new Error("In Put");
        }
    }

    public void Flush() throws Exception {
        PutBuf(wbuf);
    }
}
