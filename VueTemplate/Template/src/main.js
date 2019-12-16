import Vue from 'vue';
import App from './App.vue';
window.$ = require('jquery');
window.JQuery = require('jquery');


//Adding in Bootstrap
import BootstrapVue from 'bootstrap-vue';
import 'bootstrap/dist/css/bootstrap.css';
import 'bootstrap-vue/dist/bootstrap-vue.css';
import 'bootstrap'
import 'bootstrap/dist/css/bootstrap.min.css'
Vue.use(BootstrapVue);
//////////////////////////////

//Adding in VueX
import Vuex from 'vuex';
import store from './store';
Vue.use(Vuex)
/////////////////////////////

//Adding in Font Awesome
import { library } from '@fortawesome/fontawesome-svg-core';
import { faCoffee } from '@fortawesome/free-solid-svg-icons';
import { faHeart } from '@fortawesome/free-solid-svg-icons';
import { faRedo } from '@fortawesome/free-solid-svg-icons';
import { faSave } from '@fortawesome/free-solid-svg-icons';
import { faCopy } from '@fortawesome/free-solid-svg-icons';
import { faFileAlt } from '@fortawesome/free-solid-svg-icons';

import { FontAwesomeIcon } from '@fortawesome/vue-fontawesome';

library.add(faCoffee);
library.add(faHeart);
library.add(faRedo);
library.add(faSave);
library.add(faCopy);
library.add(faFileAlt);

Vue.component('font-awesome-icon', FontAwesomeIcon)
/////////////////////////

Vue.config.productionTip = true;

new Vue({
	store,
	render: h => h(App)
}).$mount('#app');
