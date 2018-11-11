/*
 * To change this license header, choose License Headers in Project Properties.
 * To change this template file, choose Tools | Templates
 * and open the template in the editor.
 */
package org.shareabledata;
import java.time.*;
import java.io.*;
/**
 *
 * @author Malcolm
 */
public class STimeSpan extends Serialisable {
        public final long ticks;
        public STimeSpan(Duration s)
        {
            super(Types.STimeSpan);
            ticks = s.toNanos()/1000;
        }
        STimeSpan(AStream f)  throws IOException
        {
            super(Types.STimeSpan, f);
            ticks = f.GetLong();
        }
        public void Put(AStream f) throws Exception
        {
            super.Put(f);
            f.PutLong(ticks);
        }
        public static Serialisable Get(AStream f) throws IOException
        {
            return new STimeSpan(f);
        }
        public String ToString()
        {
            return "TimeSpan "+ticks;
        }
}