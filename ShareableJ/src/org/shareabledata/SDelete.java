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
public class SDelete extends SDbObject {
        public final long table;
        public final long delpos;
        public SDelete(STransaction tr, long t, long p) 
        {
            super(Types.SDelete,tr);
            table = t;
            delpos = p;
        }
        public SDelete(SDelete r, AStream f) throws Exception
        {
            super(r,f);
            table = f.Fix(r.table);
            delpos = f.Fix(r.delpos);
            f.PutLong(table);
            f.PutLong(delpos);
        }
        SDelete(StreamBase f) throws IOException
        {
            super(Types.SDelete,f);
            table = f.GetLong();
            delpos = f.GetLong();
        }
        public static SDelete Get(SDatabase d, AStream f) throws Exception
        {
            return new SDelete(f);
        }
        public boolean Conflicts(Serialisable that)
        { 
            switch(that.type)
            {
                case Types.SUpdate:
                    return ((SUpdate)that).Defpos() == delpos;
                case Types.SRecord:
                    return ((SRecord)that).Defpos() == delpos;
            }
            return false;
        }
        public String ToString()
        {
            StringBuilder sb = new StringBuilder("Delete ");
            sb.append(Uid());
            sb.append(" of "); sb.append(STransaction.Uid(delpos));
            sb.append("["); sb.append(STransaction.Uid(table)); sb.append("]");
            return sb.toString();
        }
}
