import type {Employee} from './types';
export const payrollFields=['salary','commission','bonus','pension','fund','credits','children','currentCar','participation'];
export const known=(employee:Employee,key:string)=>employee.knownFields==null||employee.knownFields.includes(key);
export function withProfileFields(employee:Employee,patch:Partial<Employee>,forgot:string[]=[]):Employee{
 const knownFields=employee.knownFields==null?(forgot.length?payrollFields.filter(field=>!forgot.includes(field)):null):[...new Set([...employee.knownFields,...Object.keys(patch).filter(key=>payrollFields.includes(key))])].filter(key=>!forgot.includes(key));
 return {...employee,...patch,knownFields};
}
export function unsetProfileField(employee:Employee,key:string):Employee{
 return {...employee,[key]:0,knownFields:(employee.knownFields??payrollFields).filter(field=>field!==key)};
}
