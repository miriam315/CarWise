import type {Car,CarPhoto} from './types';
const views:Record<string,CarPhoto['view']>={exterior:'exterior',interior:'interior',spin:'spin','360':'spin','חוץ':'exterior','פנים':'interior'};
/** Match exact catalogue IDs, never guess between years or trims of the same model. */
export function matchCarImagePath(path:string,cars:Pick<Car,'id'>[]):{carId:string;view:CarPhoto['view']} {
 const segments=path.replaceAll('\\','/').split('/').filter(Boolean);
 const name=segments.at(-1)||'';const tokens=name.includes('__')?name.split('__'):segments;
 const index=tokens.findIndex(token=>cars.some(car=>car.id.toLowerCase()===token.toLowerCase()));
 if(index<0)return{carId:'',view:'exterior'};
 const carId=cars.find(car=>car.id.toLowerCase()===tokens[index].toLowerCase())!.id;
 const kind=tokens[index+1]?.toLowerCase();
 return{carId,view:views[kind]||'exterior'};
}
