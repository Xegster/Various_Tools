import Vue from 'vue';
import Vuex from 'vuex';
import module from './modules/module';
import module2 from './modules/module2';

Vue.use(Vuex);

//const debug = process.env.NODE_ENV !== 'production'

export default new Vuex.Store({
  modules: {
		module,
		module2
  }
})