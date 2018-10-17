/*
 * To change this license header, choose License Headers in Project Properties.
 * To change this template file, choose Tools | Templates
 * and open the template in the editor.
 */
package org.shareabledata;

/**
 *
 * @author Malcolm
 */
public class SDictBookmark<K extends Comparable,V> extends Bookmark<SSlot<K,V>> {
    public final SBookmark<K,V> _bmk;
    SDictBookmark(SBookmark<K,V> bmk,int pos) 
    { 
        super(pos);
        _bmk = bmk; 
    }
    @Override
    public Bookmark<SSlot<K, V>> Next() {
        SBookmark<K,V> b = _bmk.Next(_bmk,null);
        return (b==null)?null:new SDictBookmark<K,V>(b,b.position());
    }

    @Override
    public SSlot<K, V> getValue() {
        return ((SLeaf<K,V>)_bmk._bucket).slots[_bmk._bpos];
    }
    
}
