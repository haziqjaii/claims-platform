import { provideHttpClient } from '@angular/common/http';
import { bootstrapApplication } from '@angular/platform-browser';
import { Workspace } from './workspace';

bootstrapApplication(Workspace, { providers: [provideHttpClient()] }).catch(console.error);
