/** The address of the list of a label: `@label` cannot be the address of a folder (a folder's name is always encoded). */
export const labelPath = (name: string) => `/mail/@label/${encodeURIComponent(name)}`;
