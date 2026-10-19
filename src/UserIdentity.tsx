import {ShieldCheck,Users} from 'lucide-react';
import type {SessionUser} from './types';
export default function UserIdentity({user}:{user?:SessionUser}){
 const role=user?.role==='admin'?'מנהל מערכת':user?.role==='agent'?'נציג':'עובד';
 const label=user?.displayName||user?.email||role;
 return <div className="signed-in-user" aria-label="החשבון המחובר"><span className="avatar">{user?.role==='admin'?<ShieldCheck size={22}/>:<Users size={22}/>}</span><div><strong dir="auto">{label}</strong><small>{role}{user?.displayName&&user.email?` · ${user.email}`:''}</small></div></div>;
}
