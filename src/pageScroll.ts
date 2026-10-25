import {useLayoutEffect} from 'react';

export function scrollToPageTop(){
 window.scrollTo({top:0,left:0,behavior:'instant'});
}

export function usePageScrollReset(view:string){
 useLayoutEffect(scrollToPageTop,[view]);
}
