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
public class SInteger extends Serialisable {
        public final int value;
        public SInteger(int v)
        {
            super(Types.SInteger);
            value = v;
        }
        SInteger(Reader f) throws Exception
        {
            super(Types.SInteger, f);
            value = f.GetInt();
        }
        @Override
        public void Put(StreamBase f) throws Exception
        {
            super.Put(f);
            f.PutInt(value);
        }
        public static Serialisable Get(Reader f) throws Exception
        {
            return new SInteger(f);
        }
        public String toString()
        {
            return "Integer " + value;
        }
}
